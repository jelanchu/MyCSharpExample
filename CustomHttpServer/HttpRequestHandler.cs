using HttpMultipartParser;
using log4net;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CustomHttpServer
{
    class HttpRequestHandler
    {
        private static ILog logger = LogManager.GetLogger(typeof(Program));

        public Object[] handle(Hashtable httpHeaders, MemoryStream ms)
        {
            ArrayList responseDatas = new ArrayList();

            var parser = new MultipartFormDataParser(ms, Encoding.UTF8);
            // Multi-file access
            int i = 0;

            logger.Debug("Input face images: " + parser.Files.Count);

            foreach (var f in parser.Files)
            {
                HttpRequestDataObj reqObject = JsonConvert.DeserializeObject<HttpRequestDataObj>(parser.Parameters[i].Data);
                logger.Debug("reqHeader.uid: [ " + reqObject.capturedTime + " ]");
                logger.Debug("reqHeader.imageFilename: [ " + parser.Files.ElementAt(i).Name + " ]");
                //string filename = reqObject.imageFilename;
                //CopyStream(f.Data, filename);
                //f.Data.Flush();
                //f.Data.Close();
                
                // do
                Thread.Sleep(300);

                HttpResponseDataObj resp = new HttpResponseDataObj();
                resp.capturedTime = reqObject.capturedTime;
                resp.message = @"got " + reqObject.imageFilename;
                responseDatas.Add(resp);
                i++;
            }

            return responseDatas.ToArray(typeof(Object)) as Object[];
        }

        private void CopyStream(Stream stream, string destPath)
        {
            using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write))
            {
                stream.CopyTo(fileStream);
            }
        }
    }
}
