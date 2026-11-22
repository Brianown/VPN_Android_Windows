using System;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace LiteTunnel
{
    public sealed class TrafficResponse
    {
        public string name { get; set; }
        public string period { get; set; }
        public long rx { get; set; }
        public long tx { get; set; }
        public long updated_at { get; set; }
        public string reset_at { get; set; }
    }

    /// <summary>
    /// 从服务端 VPN 内网 API 读取当前客户端的月度流量。
    /// </summary>
    public static class TrafficStatsClient
    {
        public static TrafficResponse Fetch(string url, string token, out string error)
        {
            error = null;
            try
            {
                string separator = url.IndexOf('?') >= 0 ? "&" : "?";
                string fullUrl = url + separator + "token=" + Uri.EscapeDataString(token);
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(fullUrl);
                request.Method = "GET";
                request.Proxy = null;
                request.Timeout = 3000;
                request.ReadWriteTimeout = 3000;
                request.UserAgent = "LiteTunnel/1.0";
                request.Accept = "application/json";

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string json = reader.ReadToEnd();
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    return serializer.Deserialize<TrafficResponse>(json);
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }
}
