using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Net;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.AzureAppServices;

namespace DebugExtension
{
    public class Startup
    {
        private const int MAX_BUFFER_SIZE = 65536;
        private const int CLOSE_TASK_TIMEOUT_IN_MILLISECONDS = 5000;
        private static ILoggerFactory loggerFactory = null;
        private static ILogger _logger = null;

        public void ConfigureLogging()
        {
            if(loggerFactory == null)
            {
                loggerFactory = new LoggerFactory();

                string level = Environment.GetEnvironmentVariable("APPSVC_TUNNEL_VERBOSITY");
                LogLevel logLevel = LogLevel.Information;

                if (!string.IsNullOrWhiteSpace(level))
                {
                    if (level.Equals("info", StringComparison.OrdinalIgnoreCase))
                    {
                        logLevel = LogLevel.Information;
                    }
                    if (level.Equals("error", StringComparison.OrdinalIgnoreCase))
                    {
                        logLevel = LogLevel.Error;
                    }
                    if (level.Equals("debug", StringComparison.OrdinalIgnoreCase))
                    {
                        logLevel = LogLevel.Debug;
                    }
                    if (level.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        logLevel = LogLevel.None;
                    }
                    Console.WriteLine("Setting LogLevel to " + level);
                }

                loggerFactory.AddConsole(logLevel);
            }

            if (_logger == null)
            {
                _logger = loggerFactory.CreateLogger<Startup>();
            }
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            ConfigureLogging();
        }

        /**
        *  DebugSessionState holds the websocket and 
        *  debuggeeSocket that are part of a debug session.
        */
        class DebugSessionState
        {
            public Socket DebuggeeSocket { get; private set; }
            public WebSocket DebuggerWebSocket { get; private set; }
            public byte[] Buffer { get; private set; }

            public DebugSessionState(Socket debuggeeSocket, WebSocket webSocket)
            {
                DebuggeeSocket = debuggeeSocket;
                DebuggerWebSocket = webSocket;
                Buffer = new byte[MAX_BUFFER_SIZE];
            }

            public DebugSessionState(Socket debuggeeSocket, WebSocket webSocket, int bufferSize)
            {
                DebuggeeSocket = debuggeeSocket;
                DebuggerWebSocket = webSocket;
                Buffer = new byte[bufferSize];
            }
        }

        /**
         * OnDataReceiveFromDebuggee is the async callback called when
         * we have data from debuggeeSocket. On receiving data from debuggee,
         * we forward it on the webSocket to the debugger and issue the next
         * async read on the debuggeeSocket.
         */
        private async static void OnDataReceiveFromDebuggee(IAsyncResult result)
        {
            _logger.LogDebug("OnDataReceiveFromDebuggee called.");
            var debugSessionState = (DebugSessionState)result.AsyncState;
            try
            {
                _logger.LogDebug("OnDataReceiveFromDebuggee EndReceive called.");
                var bytesRead = debugSessionState.DebuggeeSocket.EndReceive(result);

                _logger.LogDebug("OnDataReceiveFromDebuggee EndReceive returned with BytesRead=" + bytesRead);
                if (bytesRead > 0)
                {
                    //
                    // got data from debuggee, need to write it to websocket.
                    //
                    _logger.LogDebug("OnDataReceiveFromDebuggee DebuggerWebSocketState=" + debugSessionState.DebuggerWebSocket.State);
                    if (debugSessionState.DebuggerWebSocket.State == WebSocketState.Open)
                    {
                        ArraySegment<byte> outputBuffer = new ArraySegment<byte>(debugSessionState.Buffer,
                                                                                 0,
                                                                                 bytesRead);
                        _logger.LogDebug("OnDataReceiveFromDebuggee Send to websocket: " + bytesRead);
                        await debugSessionState.DebuggerWebSocket.SendAsync(outputBuffer,
                                                                            WebSocketMessageType.Binary,
                                                                            true,
                                                                            CancellationToken.None);
                        _logger.LogDebug("OnDataReceiveFromDebuggee Sent to websocket: " + bytesRead);
                    }
                }

                //
                // issue next read from debuggee socket
                //
                _logger.LogDebug("OnDataReceiveFromDebuggee: Initiate receive from DebuggeeSocket: " + debugSessionState.DebuggeeSocket.Connected);
                if (debugSessionState.DebuggeeSocket.Connected)
                {
                    debugSessionState.DebuggeeSocket.BeginReceive(debugSessionState.Buffer,
                                                                  0,
                                                                  debugSessionState.Buffer.Length,
                                                                  SocketFlags.None,
                                                                  OnDataReceiveFromDebuggee,
                                                                  debugSessionState);
                    _logger.LogDebug("OnDataReceiveFromDebuggee: BeginReceive called...");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("OnDataReceiveFromDebuggee Exception" + ex.ToString());
                try
                {
                    Task closeTask = debugSessionState.DebuggerWebSocket.CloseAsync(WebSocketCloseStatus.InternalServerError,
                                                                   "Error communicating with debuggee.",
                                                                   CancellationToken.None);
                    closeTask.Wait(CLOSE_TASK_TIMEOUT_IN_MILLISECONDS);
                }
                catch (Exception) { /* catch all since if close fails, we need to move on. */ }

                if (debugSessionState.DebuggeeSocket != null)
                {
                    try
                    {
                        debugSessionState.DebuggeeSocket.Close();
                    }
                    catch (Exception) { /* catch all since if close fails, we need to move on. */ }
                }
            }
        }

        private async Task HandleWebSocketConnection(WebSocket webSocket, string ipAddress, int debugPort, int bufferSize)
        {
            Socket debuggeeSocket = null;
            String exceptionMessage = "Connection failure. ";
            try
            {
                //
                // Define a maximum message size this handler can receive (1K in this case) 
                // and allocate a buffer to contain the received message. 
                // This buffer will be reused for each receive operation.
                //

                DebugSessionState debugSessionState = null;

                if (debuggeeSocket == null)
                {
                    debuggeeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    if (!debuggeeSocket.Connected)
                    {
                        debuggeeSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                        debuggeeSocket.Connect(new IPEndPoint(IPAddress.Parse(ipAddress), debugPort));

                        _logger.LogInformation("HandleWebSocketConnection: New Connection to local socket: " + ipAddress + ":" + debugPort);

                        if (bufferSize > 0)
                        {
                            debugSessionState = new DebugSessionState(debuggeeSocket, webSocket, bufferSize);
                            _logger.LogInformation("HandleWebSocketConnection: Buffer Size: " + bufferSize);
                        }
                        else
                        {
                            debugSessionState = new DebugSessionState(debuggeeSocket, webSocket);
                        }

                        if(debuggeeSocket.Connected)
                        {
                            _logger.LogInformation("HandleWebSocketConnection: Connected to site debugger on " + ipAddress + ":" + debugPort);
                        }

                        _logger.LogDebug("HandleWebSocketConnection: DebuggeeSocket BeginReceive initiated...");

                        debugSessionState.DebuggeeSocket.BeginReceive(debugSessionState.Buffer,        // receive buffer
                                                                       0,                               // offset
                                                                       debugSessionState.Buffer.Length, // length of buffer
                                                                       SocketFlags.None,
                                                                       OnDataReceiveFromDebuggee,       // callback
                                                                       debugSessionState);              // state
                    }
                    else
                    {
                        // duplicate handshake.
                        // will just let the jvm/debugger decide what to do.
                        _logger.LogDebug("HandleWebSocketConnection: Duplicate connection .. ignored ...");
                    }
                }

                //
                // While the WebSocket connection remains open we run a simple loop that receives messages.
                // If a handshake message is received, connect to the debug port and forward messages to/from
                // the debuggee.
                //

                while (webSocket.State == WebSocketState.Open)
                {
                    byte[] receiveBuffer = null;

                    if (bufferSize > 0)
                    {
                        receiveBuffer = new byte[bufferSize];
                    }
                    else
                    {
                        receiveBuffer = new byte[MAX_BUFFER_SIZE];
                    }

                    WebSocketReceiveResult receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), CancellationToken.None);

                    _logger.LogDebug("HandleWebSocketConnection: Got data from websocket: " + receiveResult.Count);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("HandleWebSocketConnection: Websocket closed !!");
                        break;
                    }
                    else
                    {
                        int receivedBytes = receiveResult.Count;
                        if (receivedBytes > 0)
                        {
                            if (debuggeeSocket == null)
                            {
                                debuggeeSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                                if (!debuggeeSocket.Connected)
                                {
                                    debuggeeSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                                    debuggeeSocket.Connect(new IPEndPoint(IPAddress.Parse(ipAddress), debugPort));
                                    debugSessionState = new DebugSessionState(debuggeeSocket, webSocket);

                                    _logger.LogInformation("HandleWebSocketConnection: Connected to local socket: " + ipAddress + ":" + debugPort);

                                    debugSessionState.DebuggeeSocket.BeginReceive(debugSessionState.Buffer,        // receive buffer
                                                                                   0,                               // offset
                                                                                   debugSessionState.Buffer.Length, // length of buffer
                                                                                   SocketFlags.None,
                                                                                   OnDataReceiveFromDebuggee,       // callback
                                                                                   debugSessionState);              // state
                                }
                                else
                                {
                                    // duplicate handshake.
                                    // will just let the jvm/debugger decide what to do.
                                }
                            }

                            // if send fails, it will throw and we release all resources below.
                            debuggeeSocket.Send(receiveBuffer, 0, receivedBytes, SocketFlags.None);

                            _logger.LogDebug("HandleWebSocketConnection: Sent data to socket: " + receivedBytes);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // catch all Exceptions as we dont want the server to crash on exceptions.
                _logger.LogError("HandleWebSocket connection exception: " + e.ToString());
                exceptionMessage += e.Message;
            }
            finally
            {
                try
                {
                    Task closeTask = webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError,
                                                          exceptionMessage,
                                                          CancellationToken.None);
                    closeTask.Wait(CLOSE_TASK_TIMEOUT_IN_MILLISECONDS);
                }
                catch (Exception) { /* catch all since if close fails, we need to move on. */ }
                if (debuggeeSocket != null)
                {
                    try
                    {
                        debuggeeSocket.Close();
                        debuggeeSocket = null;
                    }
                    catch (Exception) { /* catch all since if close fails, we need to move on. */ }
                }
            }
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            ConfigureLogging();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            var webSocketOptions = new WebSocketOptions()
            {
                KeepAliveInterval = TimeSpan.FromSeconds(15)
            };
            app.UseWebSockets(webSocketOptions);

            app.Use(async (context, next) =>
            {
                int tunnelPort = -1;
                if (context.Request.Headers.ContainsKey("AppsvcTunnelPort"))
                {
                    tunnelPort = int.Parse(context.Request.Headers["AppsvcTunnelPort"].First());
                }

                int bufferSize = 65536;
                if (context.Request.Headers.ContainsKey("AppsvcTunnelBuffer"))
                {
                    bufferSize = int.Parse(context.Request.Headers["AppsvcTunnelBuffer"].First());
                }

                _logger.LogInformation("Appsvc: " + tunnelPort + " " + bufferSize);

                string ipAddress = null;
                try
                {
                    ipAddress = Environment.GetEnvironmentVariable("APPSVC_TUNNEL_IP");
                    _logger.LogInformation("HandleWebSocketConnection: Found IP Address from APPSVC_TUNNEL_IP: " + ipAddress);
                }
                catch (Exception)
                {
                }

                try
                {
                    if (ipAddress == null)
                    {
                        ipAddress = File.ReadAllText("/appsvctmp/ipaddr_" + Environment.GetEnvironmentVariable("WEBSITE_ROLE_INSTANCE_ID"));
                        _logger.LogInformation("HandleWebSocketConnection: Found IP Address from appsvctmp file: " + ipAddress);
                    }
                }
                catch (Exception)
                {
                }

                try
                {
                    if (ipAddress == null)
                    {
                        ipAddress = File.ReadAllText("/home/site/ipaddr_" + Environment.GetEnvironmentVariable("WEBSITE_ROLE_INSTANCE_ID"));
                        _logger.LogInformation("HandleWebSocketConnection: Found IP Address from share file: " + ipAddress);
                    }
                }
                catch (Exception)
                {
                }

                if (ipAddress == null)
                {
                    ipAddress = "127.0.0.1";
                }

                _logger.LogInformation("HandleWebSocketConnection: Final IP Address: " + ipAddress);

                int debugPort = 2222;

                try
                {
                    debugPort = Int32.Parse(Environment.GetEnvironmentVariable("APPSVC_TUNNEL_PORT"));

                    if (debugPort <= 0)
                    {
                        throw new Exception("Debuggee not found. Please start your site in debug mode and then attach debugger.");
                    }
                }
                catch (Exception ex)
                {
                    debugPort = 2222;
                }

                string remoteDebug = "FALSE";
                int remoteDebugPort = -1;

                try
                {
                    remoteDebug = File.ReadAllText("/appsvctmp/remotedebug_" + Environment.GetEnvironmentVariable("WEBSITE_ROLE_INSTANCE_ID"));
                    _logger.LogInformation("HandleWebSocketConnection: Found remote debug file: " + remoteDebug);

                    if(!string.IsNullOrWhiteSpace(remoteDebug) && !remoteDebug.Contains("FALSE"))
                    {
                        // remote debug is enabled
                        if(int.TryParse(remoteDebug, out remoteDebugPort))
                        {
                            debugPort = remoteDebugPort;
                            _logger.LogInformation("HandleWebSocketConnection: Found remote debug port from file: " + debugPort);
                        }
                    }
                }
                catch (Exception)
                {
                }

                if (tunnelPort > 0)
                {
                    // this is coming from client side.. override.
                    debugPort = tunnelPort;
                }

                _logger.LogInformation("HandleWebSocketConnection: Final Port: " + debugPort);

                if (context.WebSockets.IsWebSocketRequest)
                {
                    _logger.LogInformation("Got websocket request");               
                    WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    await HandleWebSocketConnection(webSocket, ipAddress, debugPort, bufferSize);
                }
                else
                {
                    if (context.Request.QueryString.HasValue && context.Request.QueryString.Value.Contains("GetStatus"))
                    {
                        _logger.LogInformation("Got a status request... connecting to " + ipAddress + ":" + debugPort);

                        try
                        {
                            using (Socket testSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                            {
                                testSocket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.NoDelay, true);
                                testSocket.Connect(new IPEndPoint(IPAddress.Parse(ipAddress), debugPort));
                                context.Response.StatusCode = 200;
                                context.Response.WriteAsync("SUCCESS:"+debugPort);
                                _logger.LogInformation("GetStats success " + ipAddress + ":" + debugPort);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex.ToString());
                            context.Response.StatusCode = 200;
                            context.Response.WriteAsync("FAILURE:" + debugPort + ":" + ex.Message);
                        }
                    }
                    else
                    {
                        context.Response.StatusCode = 400;
                    }
                }
            });
        }
    }
}
