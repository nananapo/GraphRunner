#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using GraphConnectEngine;
using GraphConnectEngine.Graphs.Value;
using GraphConnectEngine.Nodes;
using GraphRunner.Json;

namespace GraphRunner
{
    public class ExecutionEnv : IDisposable
    {
        private readonly IDictionary<int, IGraph> _graphs;
        private readonly INodeConnector _connector;
        private readonly IDictionary<string, Execution> _path2execution;

        private HttpListener _httpListener;
        
        public bool IsServerRunning { get; private set; }

        public ExecutionEnv()
        {
            _graphs = new Dictionary<int, IGraph>();
            _connector = new NodeConnector();
            _path2execution = new Dictionary<string, Execution>();
            
            IsServerRunning = false;
        }

        public bool AddGraph(GraphSetting setting)
        {
            if (_graphs.ContainsKey(setting.Id)) return false;

            var graph = CreateGraph(setting);

            if (graph != null)
            {
                _graphs[setting.Id] = graph;
                return true;
            }

            return false;
        }

        public bool RemoveGraph(int id)
        {
            if (!_graphs.ContainsKey(id))
                return false;
            
            _graphs[id].Dispose();
            _graphs.Remove(id);
            return true;
        }

        public bool ConnectNode(NodeConnection setting)
        {
            var node1 = GetNode(setting.From);
            var node2 = GetNode(setting.To);

            if (node1 == null || node2 == null)
                return false;

            return setting.Connect ? _connector.ConnectNode(node1, node2) : _connector.DisconnectNode(node1,node2);
        }

        public async Task<bool> Execute(Execution setting, Func<string, Task<bool>> func)
        {
            if (!_graphs.ContainsKey(setting.GraphId))
            {
                return false;
            }

            var graph = _graphs[setting.GraphId];
            if (graph is MyUpdateGraph updater)
            {
                await updater.Execute(func);
                return true;
            }

            return false;
        }

        public bool AddPath(BindSetting setting)
        {
            if (_path2execution.ContainsKey(setting.Path))
            {
                return false;
            }

            var exec = new Execution();
            exec.GraphId = setting.UpdaterId;
            
            _path2execution[setting.Path] = exec;
            return true;
        }

        public bool RemovePath(string path)
        {
            if (_path2execution.ContainsKey(path))
            {
                _path2execution.Remove(path);
            }
            return false;
        }

        public bool StartServer(int port)
        {
            if (IsServerRunning)
            {
                return false;
            }

            IsServerRunning = true;
            SimpleListener(new []{$@"http://+:{port}/"});
            
            return true;
        }

        // This example requires the System and System.Net namespaces.
        private async Task SimpleListener(string[] prefixes)
        {
            Console.WriteLine("Starting server...");
            
            if (!HttpListener.IsSupported)
            {
                Console.WriteLine("Windows XP SP2 or Server 2003 is required to use the HttpListener class.");
                return;
            }

            // URI prefixes are required,
            // for example "http://contoso.com:8080/index/".
            if (prefixes == null || prefixes.Length == 0)
                throw new ArgumentException("prefixes");

            // Create a listener.
            _httpListener = new HttpListener();
            // Add the prefixes.
            foreach (string s in prefixes)
            {
                _httpListener.Prefixes.Add(s);
            }
            
            _httpListener.Start();
            Console.WriteLine("Started. Type \"quit\" to exit.");
            
            while (IsServerRunning)
            {
                // Note: The GetContext method blocks while waiting for a request.
                HttpListenerContext context = await _httpListener.GetContextAsync();
                HttpListenerRequest request = context.Request;

                if (request.Url == null)
                    continue;

                var path = request.Url.LocalPath;

                if (!_path2execution.ContainsKey(path))
                    continue;

                var exec = _path2execution[path];
                
                if(!_graphs.ContainsKey(exec.GraphId))
                    continue;

                if (!(_graphs[exec.GraphId] is MyUpdateGraph updater))
                    continue;
                
                // Obtain a response object.
                HttpListenerResponse response = context.Response;
                System.IO.Stream output = response.OutputStream;
                
                ListenExec(output, response, updater);
            }
        }

        private async void ListenExec(System.IO.Stream stream, HttpListenerResponse response,MyUpdateGraph updater)
        {
            // Construct a response.
            string responseString = "";

            await updater.Execute(async (msg) =>
            {
                responseString += msg;
                return true;
            });
            
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);

            // Get a response stream and write the response to it.
            response.ContentLength64 = buffer.Length;
            stream.Write(buffer, 0, buffer.Length);

            // You must close the output stream.
            stream.Close();
        }

        public INode? GetNode(NodeInfo info)
        {
            return GetNode(info.GraphId, info.GetNodeType(), info.Index);
        }

        public INode? GetNode(int graphId, NodeType type, int index)
        {
            if (!_graphs.ContainsKey(graphId))
            {
                return null;
            }

            var graph = _graphs[graphId];

            switch (type)
            {
                case NodeType.InProcess:
                    return graph.InProcessNodes.Count <= index ? null : graph.InProcessNodes[index];
                case NodeType.OutProcess:
                    return graph.OutProcessNodes.Count <= index ? null : graph.OutProcessNodes[index];
                case NodeType.InItem:
                    return graph.InItemNodes.Count <= index ? null : graph.InItemNodes[index];
                case NodeType.OutItem:
                    return graph.OutItemNodes.Count <= index ? null : graph.OutItemNodes[index];
            }

            return null;
        }


        private IGraph? CreateGraph(GraphSetting setting)
        {
            switch (setting.Type)
            {
                case "Updater":
                    return new MyUpdateGraph(_connector, new SerialSender());
                case "PrintText":
                    return new MyDebugTextGraph(_connector);
                case "Value":

                    if (!(setting.Setting.ContainsKey("Type") &&
                          setting.Setting.ContainsKey("Value")))
                        return null;

                    switch (setting.Setting["Type"])
                    {
                        case "Int":
                            return new ValueGraph<int>(_connector, int.Parse(setting.Setting["Value"]));
                    }

                    break;
            }

            return null;
        }

        public void Dispose()
        {
            _httpListener?.Stop();
            IsServerRunning = false;
        }
    }
}