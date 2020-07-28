using Nancy;
using Nancy.Responses;
using System;
using System.Collections.Generic;
using System.Text;

namespace TestZennoPlugin
{
    public class ZennoPluginHost : Nancy.NancyModule
    {
        public ZennoPluginHost(String accessUserName, String accessPassword, PluginLogicService service) : base("/zennoplugin")
        {
            // TODO: This is bad solution but... no time.
            var expectedBasicAuthHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accessUserName}:{accessPassword}"));
            Before.AddItemToStartOfPipeline(ctx =>
            {
                if (String.IsNullOrWhiteSpace(Context.Request.Headers.Authorization))
                    return new HtmlResponse(HttpStatusCode.Unauthorized);

                var authParts = Context.Request.Headers.Authorization.Split(' ');
                if(authParts.Length != 2 || authParts[0].ToLower() != "basic" || authParts[1] != expectedBasicAuthHeader)
                    return new HtmlResponse(HttpStatusCode.Unauthorized);

                return null;
            });

            Get("/projects", (_) =>
            {
                return "";
            });

            //new Action<>

            
        }
    }
}
