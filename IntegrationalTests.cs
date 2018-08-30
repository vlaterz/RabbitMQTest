using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RabbitMQ.Client;
using Proxy;

namespace IntegrationalTests
{
    [TestClass]
    public class IntegrationalTests
    {

        [TestMethod]ы
        public void DisposeConnectionTest()
        {
            ExecuteNetworkRepairTest(proxy =>
                proxy.Execute(connection =>
                    connection.Dispose()));
        }

        [TestMethod]
        public void DropTraffic_ClientToRemote_Test()
        {

            ExecuteNetworkRepairTest(proxy =>
                proxy.Execute(connection => connection.CopyClientToRemote.DropTraffic(true)));

        }

        [TestMethod]
        public void SuspendTransmission_ClientToRemote_Test()
        {

            ExecuteNetworkRepairTest(proxy =>
                proxy.Execute(connection => connection.CopyClientToRemote.SuspendTransmission(true)));

        }

        [TestMethod]
        public void DropTraffic_RemoteToClient_Test()
        {

            ExecuteNetworkRepairTest(proxy =>
                proxy.Execute(connection => connection.CopyRemoteToClient.DropTraffic(true)));
        }

        [TestMethod]
        public void SuspendTransmission_RemoteToClient_Test()
        {

            ExecuteNetworkRepairTest(proxy =>
                proxy.Execute(connection => connection.CopyRemoteToClient.SuspendTransmission(true)));
        }


        [TestMethod]
        public void DropTaffic_BothConnections_Test()
        {
            ExecuteNetworkRepairTest(proxy => proxy.Execute(connection =>
            {
                connection.CopyRemoteToClient.DropTraffic(true);
                connection.CopyClientToRemote.DropTraffic(true);
            }));
        }

        [TestMethod]
        public void SuspendClientToRemoteDropRemoteToClientTest()
        {
            ExecuteNetworkRepairTest(proxy => proxy.Execute(connection =>
            {
                connection.CopyRemoteToClient.DropTraffic(true);
                connection.CopyClientToRemote.SuspendTransmission(true);
            }));
        }

        [TestMethod]
        public void SuspendRemoteToClientDropClientToRemoteTest()
        {
            ExecuteNetworkRepairTest(proxy => proxy.Execute(connection =>
            {
                connection.CopyRemoteToClient.SuspendTransmission(true);
                connection.CopyClientToRemote.DropTraffic(true);
            }));
        }

        [TestMethod]
        public void SuspendTransmission_BothConnections_Test()
        {
            ExecuteNetworkRepairTest(proxy => proxy.Execute(connection =>
            {
                connection.CopyRemoteToClient.SuspendTransmission(true);
                connection.CopyClientToRemote.SuspendTransmission(true);
            }));
        }


        private void ExecuteNetworkRepairTest(Action<TcpDistruptingProxy> distruptionScenario)
        {
            Console.WriteLine(typeof(ConnectionFactory).Assembly.GetName().FullName);
            const int targetPort = 5672;
            const int proxyPort = 56721;
            const string targetHost = "127.0.0.1";

            var errros = 0;

            var messages = new List<string>();
            var gotMessages = new List<string>();
            string queueName = $"mytestqueueoverproxy_{Guid.NewGuid():N}";

            using (var distruptingProxy = new TcpDistruptingProxy(targetHost, targetPort, proxyPort))
            {
                var cf = new ConnectionFactory
                {
                    Uri = new Uri($"amqp://127.0.0.1:{proxyPort}/"),
                    HostName = "127.0.0.1",
                    Port = proxyPort,
                    UserName = "guest",
                    Password = "guest",
                    AutomaticRecoveryEnabled = true,
                    RequestedHeartbeat = 2,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                };

                var reconnectionCount = 0;
                var disconnectionCount = 0;
                using (var connection = cf.CreateConnection())
                {
                    connection.ConnectionShutdown += (sender, args) => disconnectionCount++;
                    connection.RecoverySucceeded += (sender, args) => reconnectionCount++;

                    using (var model = connection.CreateModel())
                    {
                        model.QueueDeclare(queueName, false, false, false, null);
                        model.ConfirmSelect();
                        for (var i = 0; i < 100000; i++) //100000 messages
                        {
                            if (i == 10) distruptionScenario(distruptingProxy);

                            var message = $"message_{Guid.NewGuid()}_{i}";
                            try
                            {
                                var body = Encoding.UTF8.GetBytes(message);
                                model.BasicPublish(exchange: "",
                                    routingKey: queueName,
                                    basicProperties: null,
                                    body: body);
                                model.WaitForConfirms();

                                messages.Add(message);
                                if (errros > 0) break;
                            }
                            catch (Exception)
                            {
                                ++errros;
                            }

                            Thread.Sleep(50);
                        }

                        Assert.AreNotEqual(0, disconnectionCount);
                        Assert.AreNotEqual(0, reconnectionCount);

                    }
                }
            }

            var cf2 = new ConnectionFactory
            {
                Uri = new Uri($"amqp://{targetHost}:{targetPort}/"),
                HostName = targetHost,
                Port = targetPort,
                UserName = "guest",
                Password = "guest",
                AutomaticRecoveryEnabled = true,
                RequestedHeartbeat = 2,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5)
            };
            using (var cn2 = cf2.CreateConnection())
            {
                using (var ch2 = cn2.CreateModel())
                {
                    for (;;)
                    {
                        var basicGetResult = ch2.BasicGet(queueName, true);
                        if (basicGetResult == null) break;
                        gotMessages.Add(Encoding.UTF8.GetString(basicGetResult.Body));
                    }

                    ch2.QueueDelete(queueName);
                }
            }

            Assert.AreNotEqual(0, errros);
            Assert.IsTrue(messages.Count > 10);
            Assert.AreEqual(messages.Count, gotMessages.Count);
            for (var i = 0; i < messages.Count; i++)
                Assert.AreEqual(messages[i], gotMessages[i]);
        }
    }
}
