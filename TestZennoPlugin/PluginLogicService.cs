using System;
using System.Net;
using System.IO;
using System.Web.Script.Serialization;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;
using IAsyncServiceProvider = Microsoft.VisualStudio.Shell.IAsyncServiceProvider;
using EnvDTE80;
using System.Net.Http;

namespace TestZennoPlugin
{
    public class PluginLogicService : SPluginLogicService, IPluginLogicService
    {
        // TODO: Should be moved to settings window.
        readonly Int32 ServicePort = 9889;
        readonly ServiceType WebServiceType = ServiceType.Http;
        readonly String AccessLogin = "Hi";
        readonly String AccessPassword = "Zenno";

        readonly IAsyncServiceProvider asyncServiceProvider;
        IVsOutputWindowPane outputPanel = null;
        CancellationToken cancellationToken;        

        public PluginLogicService(IAsyncServiceProvider provider)
        {
            asyncServiceProvider = provider;
        }        

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            await TaskScheduler.Default;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        }

        #region Log

        async Task Log(String message)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (outputPanel == null)
                outputPanel = await asyncServiceProvider.GetServiceAsync(typeof(SVsGeneralOutputWindowPane)) as IVsOutputWindowPane;

            outputPanel.OutputStringThreadSafe(message + Environment.NewLine);
        }
        #endregion


        #region Control server

        /// <summary>
        /// Start host.
        /// </summary>
        /// <returns></returns>
        public async Task StartHost()
        {
            await Log($"Start listen. {nameof(AccessLogin)}:{AccessLogin}, {nameof(AccessPassword)}:{AccessPassword}, {nameof(ServicePort)}:{ServicePort}");

            if (WebServiceType == ServiceType.Http)
            {
                using (var listener = new HttpListener())
                {
                    listener.AuthenticationSchemes = AuthenticationSchemes.Basic;
                    listener.Prefixes.Add($"http://localhost:{ServicePort}/");
                    listener.Prefixes.Add($"http://127.0.0.1:{ServicePort}/");
                    listener.Start();
                    var expectedBasicAuthHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{AccessLogin}:{AccessPassword}"));
                    // TODO: Need correct dispose.
                    await Task.Run(() =>
                    {
                        while (true)
                        {
                            ////blocks until a client has connected to the server
                            var result = listener.BeginGetContext(ListenerCallback, listener);
                            result.AsyncWaitHandle.WaitOne();
                        }
                    });

                    String SerializeToJson(Object obj)
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        return js.Serialize(obj);
                    }

                    Dictionary<String, Object> DeSerializeToJson(String obj)
                    {
                        JavaScriptSerializer js = new JavaScriptSerializer();
                        return js.Deserialize<Dictionary<String, Object>>(obj);
                    }

                    HttpStatusCode IsAuthenticate(String authHeaderValue)
                    {
                        // TODO: Bad practicle. Temporary.

                        if (String.IsNullOrWhiteSpace(authHeaderValue))
                            return HttpStatusCode.Unauthorized;

                        var authParts = authHeaderValue.Split(' ');
                        if (authParts.Length != 2 || authParts[0].ToLower() != AuthenticationSchemes.Basic.ToString().ToLower())
                            return HttpStatusCode.Unauthorized;

                        if (authParts[1] != expectedBasicAuthHeader)
                            return HttpStatusCode.Forbidden;

                        return HttpStatusCode.OK;
                    }

                    

                    async void ListenerCallback(IAsyncResult result)
                    {
                        var context = listener.EndGetContext(result);
                        var responseCode = IsAuthenticate(context.Request.Headers.Get(HttpRequestHeader.Authorization.ToString()));
                        if (responseCode != HttpStatusCode.OK)
                        {
                            context.Response.StatusCode = (Int32)responseCode;
                            context.Response.Close();
                            return;
                        }

                        Boolean CheckHttpMethod(String expectedHttpMethod)
                        {
                            var correctMethod = context.Request.HttpMethod == expectedHttpMethod;
                            if (!correctMethod)
                                responseCode = HttpStatusCode.MethodNotAllowed;

                            return correctMethod;
                        }

                        var response = String.Empty;
                        try
                        {
                            var strData = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding).ReadToEnd();
                            switch (context.Request.Url.LocalPath)
                            {
                                case "/ProjectsNames":
                                    {
                                        if (CheckHttpMethod(HttpMethod.Get.ToString()))
                                            response = SerializeToJson(await GetProjectNames());

                                        break;
                                    }
                                case "/WriteTextByPosition":
                                    {
                                        if (CheckHttpMethod(HttpMethod.Post.ToString()))
                                        {
                                            var parameters = DeSerializeToJson(strData);
                                            await WriteTextByPosition((String)parameters["text"], (Int32)parameters["position"]);
                                        }
                                        break;
                                    }
                                case "/AddFile":
                                    {
                                        if (CheckHttpMethod(HttpMethod.Post.ToString()))
                                        {
                                            var parameters = DeSerializeToJson(strData);
                                            await AddFile((String)parameters["templateName"], (String)parameters["language"], (String)parameters["name"], (String)parameters["toProjectShortName"]);
                                        }
                                        break;
                                    }
                                case "/AddNugetPackage":
                                    {
                                        if (CheckHttpMethod(HttpMethod.Post.ToString()))
                                        {
                                            var parameters = DeSerializeToJson(strData);
                                            await AddNugetPackage((String)parameters["name"], (String)parameters["url"], (String)parameters["toProjectShortName"]);
                                        }
                                        break;
                                    }
                            }

                            context.Response.StatusDescription = "OK";
                        }
                        catch (Exception ex)
                        {
                            responseCode = HttpStatusCode.InternalServerError;
                            response = ex.Message;
                        }

                        context.Response.StatusCode = (Int32)responseCode;
                        if (response.Length > 0)
                            using (var wr = new StreamWriter(context.Response.OutputStream))
                                await wr.WriteAsync(response);
                        context.Response.Close();
                    }
                }
            }
            else if (WebServiceType == ServiceType.WebSocket)
            {
                throw new NotImplementedException($"{nameof(StartHost)}. {ServiceType.WebSocket} not implemented");
            }
        }

        #endregion


        #region Helpers

        async Task AddNugetPackage(String name, String url, String toProjectShortName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // I think that console is a best way.
            var dte = await asyncServiceProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE;
            var commandTemplate = "Install-Package {0} -Project \"{1}\"";
            var command = String.Empty;
            // Example Glimpse | Glimpse -Version 1.0.0
            // Example https://globalcdn.nuget.org/packages/microsoft.aspnet.mvc.5.2.3.nupkg

            if (!String.IsNullOrWhiteSpace(name))
                command = String.Format(commandTemplate, name, toProjectShortName);            
            else if (!String.IsNullOrWhiteSpace(name))
                command = String.Format(commandTemplate, url, toProjectShortName);

            await Log($"Install package command: '{command}'.");

            if (!String.IsNullOrWhiteSpace(command))
                dte.ExecuteCommand("View.PackageManagerConsole", command);
        }

        async Task AddFile(string templateName, string language, string name, String toProjectShortName)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dte = await asyncServiceProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE;
            var projectsEnumerator = dte.Solution.Projects.GetEnumerator();
            Project foundProject = null;
            while (projectsEnumerator.MoveNext())
            {
                var curProj = (Project)projectsEnumerator.Current;
                if (curProj.Name == toProjectShortName)
                {
                    foundProject = curProj;
                    break;
                }
            }
            if (foundProject == null)
                throw new Exception($"Project '{toProjectShortName}' not found.");

            var sol2 = dte.Solution as Solution2;
            string templateFileName = sol2.GetProjectItemTemplate(templateName, language);
            if (String.IsNullOrWhiteSpace(templateFileName))
                throw new Exception($"Template {templateName} for 'language' not found.");

            var projectItemsEnumerator = foundProject.ProjectItems.GetEnumerator();
            while (projectItemsEnumerator.MoveNext())
            {
                var curProjItem = (ProjectItem)projectItemsEnumerator.Current;
                if (curProjItem.Name == name)
                {
                    throw new Exception($"Project item '{name} already exists.'");
                }
            }
            foundProject.ProjectItems.AddFromTemplate(templateFileName, name);
            foundProject.Save();
        }

        async Task WriteTextByPosition(String text, Int32 position)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dte = await asyncServiceProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE;
            if (dte.ActiveDocument == null)
                throw new Exception($"No active document.");

            
            var selection = dte.ActiveDocument.Selection as TextSelection;
            selection.SelectAll();
            var curText = selection.Text;
            curText = curText.Insert(Math.Min(position - 1, curText.Length), text);
            selection.Text = curText;
            selection.SmartFormat();
            dte.ActiveDocument.Save();
        }

        async Task<Dictionary<String, String>> GetProjectNames()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var dte = await asyncServiceProvider.GetServiceAsync(typeof(EnvDTE.DTE)) as DTE;
            var result = new Dictionary<String, String>();

            if (dte == null && dte.Solution == null && dte.Solution.Projects.Count == 0)
                return result;


            var projectsEnumerator = dte.Solution.Projects.GetEnumerator();
            while(projectsEnumerator.MoveNext())
            {
                var curProj = (Project)projectsEnumerator.Current;
                result.Add(curProj.UniqueName, curProj.Name);
            }

            return result;
        }

        /*
         try
            {
                var project = dte.Solution.Projects. //.OfType<EnvDTE.Project>().ToList();

                foreach (EnvDTE.ProjectItem projectItem in project.ProjectItems)
                {
                    if (projectItem.Name.EndsWith(".cs"))
                    {
                        OpenProjectItemInView(projectItem, guid_microsoft_csharp_editor_with_encoding,
                           Microsoft.VisualStudio.VSConstants.LOGVIEWID.Code_guid);
                    }
                    else if (projectItem.Name.EndsWith(".vb"))
                    {
                        OpenProjectItemInView(projectItem, guid_microsoft_visual_basic_editor_with_encoding,
                           Microsoft.VisualStudio.VSConstants.LOGVIEWID.Code_guid);
                    }
                    else if (projectItem.Name.EndsWith(".txt"))
                    {
                        OpenProjectItemInView(projectItem, guid_source_code_text_editor,
                           Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_guid);
                    }

                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.ToString());
            }
         */

        #endregion


    }

    public interface SPluginLogicService
    {
    }

    public interface IPluginLogicService
    {
        Task StartHost();
    }
}
