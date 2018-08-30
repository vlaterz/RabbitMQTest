using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Proxy
{
    public class TcpDistruptingProxy : IDisposable
    {
        public class Streamer
        {
            private readonly Socket _fromSocket;
            private readonly Socket _toSocket;
            private volatile bool _dropTraffic;
            private readonly ManualResetEventSlim _suspendLatch = new ManualResetEventSlim(true);

            public void DropTraffic(bool value)
            {
                _dropTraffic = value;
            }

            public void SuspendTransmission(bool value)
            {
                if (value)
                    _suspendLatch.Reset();
                else
                    _suspendLatch.Set();
            }


            public Streamer(Socket fromSocket, Socket socket)
            {
                _fromSocket = fromSocket;
                _toSocket = socket;
            }

            //Шлём в конечный сокет
            public void DoCopy()
            {
                var buffer = new byte[4096];
                try
                {
                    for (; ; )
                    {
                        _suspendLatch.Wait();
                        var bytesRead = _fromSocket.Receive(buffer);
                        if (bytesRead <= 0) break;
                        _suspendLatch.Wait();
                        if (_dropTraffic) continue;
                        _toSocket.Send(buffer, 0, bytesRead, SocketFlags.None);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("(Streamer::DoCopy)" + e);

                    //throw;
                }
            }
        }
        public class ClientConnection : IDisposable
        {
            private readonly TcpDistruptingProxy _tcpDistruptingProxy;
            private readonly Socket _client;
            private readonly Socket _remote;
            public readonly Streamer CopyClientToRemote;
            public readonly Streamer CopyRemoteToClient;


            public ClientConnection(TcpDistruptingProxy tcpDistruptingProxy, Socket client)
            {
                _tcpDistruptingProxy = tcpDistruptingProxy;
                _client = client;
                _remote = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                tcpDistruptingProxy.RegisterClient(this);
                _remote.Connect(tcpDistruptingProxy.TargetHost, tcpDistruptingProxy.TargetPort);
                CopyClientToRemote = StartCopy(client, _remote);
                CopyRemoteToClient = StartCopy(_remote, client);
            }



            private Streamer StartCopy(Socket fromSocket, Socket toSocket)
            {
                var thread = new Thread(StartCopyThread)
                {
                    Name = "StartCopyThread"
                };
                var streamer = new Streamer(fromSocket, toSocket);
                thread.Start(streamer);
                return streamer;
            }

            private void StartCopyThread(object obj)
            {
                ((Streamer)obj).DoCopy();
                Dispose();
            }

            public void Dispose()
            {
                try
                {
                    _tcpDistruptingProxy.UnRegisterClient(this);
                    _remote.Dispose();
                    _client.Dispose();
                }
                catch (Exception e)
                {
                    Console.WriteLine("ClientConnection::Dispose" + e);
                    // throw;
                }

            }
        }


        public readonly string TargetHost;
        public readonly int TargetPort;
        private readonly Socket _listenerSocket;

        public TcpDistruptingProxy(string targetHost, int targetPort, int localPoint)
        {
            TargetHost = targetHost;
            TargetPort = targetPort;
            _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenerSocket.Bind(new IPEndPoint(IPAddress.Loopback, localPoint));
            _listenerSocket.Listen(16);
            var acceptThread = new Thread(StartAcceptThread)
            {
                Name = "AcceptThread"
            };
            acceptThread.Start();
        }

        //Слушаем порт
        private void StartAcceptThread()
        {
            while (true)
            {
                var client = _listenerSocket.Accept();
                // ReSharper disable once ObjectCreationAsStatement
                new ClientConnection(this, client);
            }
            // ReSharper disable once FunctionNeverReturns
        }

        public void Execute(Action<ClientConnection> action)
        {
            try
            {
                ClientConnection[] array;
                lock (_syncRoot)
                {
                    array = _clientProcessors.ToArray();
                }

                foreach (var clientProcessor in array)
                {
                    action(clientProcessor);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("EXECUTE():"+e);
                //throw;
            }
        }

        private readonly List<ClientConnection> _clientProcessors = new List<ClientConnection>();
        private readonly object _syncRoot = new object();

        public void UnRegisterClient(ClientConnection clientConnection)
        {
            lock (_syncRoot)
            {
                _clientProcessors.Remove(clientConnection);
            }
        }

        public void RegisterClient(ClientConnection clientConnection)
        {
            lock (_syncRoot)
            {
                _clientProcessors.Add(clientConnection);
            }
        }

        public void Dispose()
        {
            try
            {
                _listenerSocket.Dispose();
                Execute(connection => connection.Dispose());
            }
            catch (Exception e)
            {
                Console.WriteLine("Dispose():" + e);
                //throw;
            }
           
        }

       
    }
}
