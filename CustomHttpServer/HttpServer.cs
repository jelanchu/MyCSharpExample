using CustomHttpServer;
using HttpServer;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HttpServer
{
    public class HttpServer
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(HttpServer));

        private int port;
        private System.Net.IPAddress ipaddress;
        private TcpListener listener;
        private bool isActive = true;

        private BlockingCollection<TcpClient> RequestQueue = new BlockingCollection<TcpClient>();
        private int numOfWorker;
        private Queue<HttpProcess> httpProcessQueue = new Queue<HttpProcess>(); 

        public HttpServer(string ipString, int httpPort, int numOfWorker)
        {
            this.ipaddress = System.Net.IPAddress.Parse(ipString);
            this.port = httpPort;
            for (int i = 0; i < numOfWorker; i++ )
            {
                HttpProcess process = new HttpProcess(RequestQueue);
                process.Launch();
                httpProcessQueue.Enqueue(process);
            }
        }

        public void Dispose()
        {
            foreach (HttpProcess process in httpProcessQueue)
            {
                process.Dispose();
            }
            httpProcessQueue.Clear();

            foreach (TcpClient client in RequestQueue) {
                client.Close();
            }
            RequestQueue.Dispose();
            isActive = false;
        }

        public void DoWork()
        {
            try
                {
                    listener = new TcpListener(ipaddress, port);
                    listener.Start();
                    while (isActive)
                    {
                        TcpClient socket = listener.AcceptTcpClient();
                        
                        if (RequestQueue.Count > numOfWorker * 2) {
                            StreamWriter outputStream = new StreamWriter(new BufferedStream(socket.GetStream()));
                            HttpResponse.writeUnavalible(outputStream);
                            outputStream.Flush();
                            socket.Close();
                            continue;
                        }
                        RequestQueue.Add(socket);
                    }
                }
                catch (Exception e)
                {
                    listener.Stop();
                    logger.Error(e.ToString(), e);
                    Dispose();
                    Environment.Exit(-99);
                }
            }
    }

    class HttpProcess 
    {
        private static readonly ILog logger = LogManager.GetLogger(typeof(HttpProcess));

        private TcpClient socket;
        private Thread thread;
        private bool isActive = true;

        private Stream inputStream;
        private StreamWriter outputStream;

        private String http_method;
        private String http_url;
        private String http_protocol_versionstring;
        private Hashtable httpHeaders = new Hashtable();

        private static int MAX_POST_SIZE = 10 * 1024 * 1024; // 10MB
        private const int BUF_SIZE = 819200;  // buffer size of read image
        private BlockingCollection<TcpClient> requestQueue;

        public HttpProcess(BlockingCollection<TcpClient> requestQueue)
        {
            this.requestQueue = requestQueue;
        }
        public void Launch()
        {
            thread = new Thread(new ThreadStart(doWork));
            thread.Start();
        }
        public void Dispose()
        {
            socket.Close();
            thread.Interrupt();
            isActive = false;
        }
        private void doWork()
        {
            while (isActive)
            {
                socket = requestQueue.Take();

                // init
                NetworkStream stream = socket.GetStream();
                stream.ReadTimeout = 5000;
                inputStream = new BufferedStream(stream);
                outputStream = new StreamWriter(new BufferedStream(socket.GetStream()));

                Object[] rdatas = null;
                // handle request
                try
                {
                    parseRequest();
                    parseHeader();
                    //Tool.WriteLine("http_method: " + http_method);
                    //Tool.WriteLine("http_url: " + http_url);
                    if (http_method.Equals("POST"))
                    {
                        if (http_url.Contains("/test/"))
                        {
                            rdatas = (Object[])handleMultipartFormData();
                        }
                    }

                    outputStream.Flush();
                }
                catch (IOException ioe)
                {
                    logger.Warn(ioe.ToString(), ioe);
                }
                catch (Exception e)
                {
                    logger.Warn(e.ToString(), e);
                    HttpResponse.writeFailure(outputStream);

                    outputStream.Flush();
                }
                finally
                {
                    inputStream = null;
                    outputStream = null;
                    socket.Close();
                    socket = null;
                }
            }
        }

        public void parseRequest()
        {
            String request = streamReadLine(inputStream);
            string[] tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                logger.Warn("Invalid http request line: " + request);
                throw new Exception("Invalid http request line");
            }
            http_method = tokens[0].ToUpper();
            http_url = tokens[1];
            http_protocol_versionstring = tokens[2];
        }

        public void parseHeader()
        {
            String line;
            while ((line = streamReadLine(inputStream)) != null)
            {
                if (line.Equals(""))
                {
                    return;
                }

                int separator = line.IndexOf(':');
                if (separator == -1)
                {
                    logger.Warn("Invalid http header line: " + line);
                    throw new Exception("Invalid http header line: " + line);
                }
                String name = line.Substring(0, separator);
                int pos = separator + 1;
                while ((pos < line.Length) && (line[pos] == ' '))
                {
                    pos++; // skip spaces
                }

                string value = line.Substring(pos, line.Length - pos);
                //Tool.WriteLine("header [ " + name + " : " + value + " ]");
                httpHeaders[name] = value;
            }
        }

        /**
         Tools
         */
        private string streamReadLine(Stream inputStream)
        {
            int next_char;
            string data = "";
            while (true)
            {
                next_char = inputStream.ReadByte();
                if (next_char == '\n') { break; }
                if (next_char == '\r') { continue; }
                if (next_char == -1) { throw new Exception("streamReadLine: connect broken while reading"); };
                data += Convert.ToChar(next_char);
            }
            return data;
        }

        private Object[] handleMultipartFormData()
        {
            Object[] rdatas = null;

            int content_len = 0;
            MemoryStream ms = new MemoryStream();
            if (this.httpHeaders.ContainsKey("Content-Length"))
            {
                content_len = Convert.ToInt32(this.httpHeaders["Content-Length"]);
                if (content_len > MAX_POST_SIZE)
                {
                    logger.Warn(String.Format("POST Content-Length({0}) too big for this simple server", content_len));
                    throw new Exception(
                        String.Format("POST Content-Length({0}) too big for this simple server", content_len));
                }
                // read image 
                byte[] buf = new byte[BUF_SIZE];
                int to_read = content_len;
                while (to_read > 0)
                {
                    //Tool.logDebug("start inputStream.Read");
                    try
                    {
                        int numread = this.inputStream.Read(buf, 0, Math.Min(BUF_SIZE, to_read));
                        if (numread == 0)
                        {
                            if (to_read == 0)
                            {
                                break;
                            }
                            else
                            {
                                logger.Warn("client disconnected during post");
                                throw new Exception("Client disconnected during post");
                            }
                        }
                        to_read -= numread;
                        ms.Write(buf, 0, numread);
                    }
                    catch (Exception e)
                    {
                        logger.Warn(e);
                        content_len = 0;
                        break;
                    }
                }
                ms.Seek(0, SeekOrigin.Begin);
            }
            if (content_len == 0)
            {
                logger.Warn("Content-Length is 0");
                HttpResponse.writeFailure(outputStream);

            }
            else
            {
                HttpRequestHandler handler = new HttpRequestHandler();
                rdatas = (Object[])handler.handle(httpHeaders, ms);

                if (rdatas == null || rdatas.Length == 0)
                {
                    HttpResponse.writeSuccess(outputStream, "text/html");
                }
                else
                {
                    HttpResponse.writeSuccess(outputStream, rdatas, "text/html");
                }
            }
            ms.Dispose();
            ms.Close();
            ms = null;

            return rdatas;
        }   
    }

    class HttpResponse
    {
        private static ILog logger = LogManager.GetLogger(typeof(HttpResponse));
        public static void writeUnavalible(StreamWriter outputStream)
        {
            
            // this is an http 404 failure response
            outputStream.WriteLine("HTTP/1.0 Service Unavalible");
            // these are the HTTP headers
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here

            outputStream.WriteLine(""); // this terminates the HTTP headers.
            logger.Warn("HTTP/1.0 503 Service Unavalible");
        }
        public static void writeSuccess(StreamWriter outputStream, string content_type = "text/html")
        {
            //writeSuccess(content_type);
            // this is the successful HTTP response line
            outputStream.WriteLine("HTTP/1.0 200 OK");

            // these are the HTTP headers...          
            outputStream.WriteLine("Content-Type: " + content_type);
            outputStream.WriteLine("Connection: close");

            // this terminates the HTTP headers.. everything after this is HTTP body..
            outputStream.WriteLine("");

            //Tool.WriteLine("HTTP Response - insertFace Request finish");
        }

        public static void writeSuccess(StreamWriter outputStream, Object[] datas, string content_type = "text/html")
        {
            // this is the successful HTTP response line
            outputStream.WriteLine("HTTP/1.0 200 OK");

            // these are the HTTP headers...          
            outputStream.WriteLine("Content-Type: " + content_type);
            outputStream.WriteLine("Connection: close");

            // ..add your own headers here if you like
            var json = JsonConvert.SerializeObject(datas);
            outputStream.WriteLine("Response: " + json);
            logger.Debug("Response: " + json);

            // this terminates the HTTP headers.. everything after this is HTTP body..
            outputStream.WriteLine("");

            //Tool.WriteLine("HTTP Response - " + json);
        }

        public static void writeFailure(StreamWriter outputStream)
        {
            // this is an http 404 failure response
            outputStream.WriteLine("HTTP/1.0 404 File not found");
            // these are the HTTP headers
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here

            outputStream.WriteLine(""); // this terminates the HTTP headers.
            logger.Warn("HTTP/1.0 404 File not found");
        }
    }
}
