namespace JustCopy.Sockets
{
    using System;
    using System.Net.Sockets;

    public interface IAcceptHandler
    {
        void Accepted(Socket socket);
    }

    public class ServerSocket : IDisposable
    {
        private readonly IAcceptHandler acceptHandler;
        private readonly Socket serverSocket;
        private SocketAsyncEventArgs[]? acceptEventArgsPool;

        public ServerSocket(Socket socket, IAcceptHandler handler)
        {
            serverSocket = socket;
            acceptHandler = handler;
        }

        public int AcceptCount { get; set; } = 1;

        public void ListenAndServe()
        {
            if (serverSocket.LocalEndPoint is null)
            {
                throw new InvalidOperationException("Server socket must be bound to an endpoint before listening.");
            }

            acceptEventArgsPool = new SocketAsyncEventArgs[AcceptCount];
            for (var i = 0; i < AcceptCount; i++)
            {
                var eventArgs = new SocketAsyncEventArgs();
                eventArgs.Completed += ProcessAccept;
                acceptEventArgsPool[i] = eventArgs;
            }

            for (var i = 0; i < AcceptCount; i++)
            {
                DoAccept(acceptEventArgsPool[i]);
            }
        }

        private void DoAccept(SocketAsyncEventArgs asyncEventArgs)
        {
            if (!serverSocket.AcceptAsync(asyncEventArgs))
            {
                ProcessAccept(null, asyncEventArgs);
            }
        }

        private void ProcessAccept(object? ignored, SocketAsyncEventArgs asyncEventArgs)
        {
            do
            {
                var acceptSocket = asyncEventArgs.AcceptSocket;
                asyncEventArgs.AcceptSocket = null;

                acceptHandler.Accepted(acceptSocket!);

                if (serverSocket.AcceptAsync(asyncEventArgs))
                {
                    break;
                }
            } while (true);
        }

        public void Dispose()
        {
            serverSocket.Dispose();
        }
    }
}
