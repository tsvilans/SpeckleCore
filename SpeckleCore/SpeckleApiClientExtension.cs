﻿using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using WebSocketSharp;

namespace SpeckleCore
{
    [Serializable]
    public enum ClientRole { Sender, Receiver, Mixed };

    public class SpeckleEventArgs : EventArgs
    {
        public string EventName { get; set; }
        public string EventData { get; set; }
        public dynamic EventObject { get; set; }
    }

    public delegate void SpeckleEvent(object source, SpeckleEventArgs e);

    [Serializable]
    public partial class SpeckleApiClient : ISerializable
    {
        public string StreamId { get; private set; }
        public string ClientId { get; private set; }

        public User User { get; private set; }
        public DataStream Stream { get; set; }

        public ClientRole? Role { get; private set; } = null;

        public bool IsAuthorized { get; private set; } = false;
        public bool IsPersistent { get; private set; } = false;

        WebSocket WebsocketClient;
        public bool WsConnected { get; private set; } = false;
        public bool IsDisposed { get; private set; } = false;
        public bool IsConnected { get; private set; } = false;

        public event SpeckleEvent OnError;
        public event SpeckleEvent OnReady;
        public event SpeckleEvent OnWsMessage;
        public event SpeckleEvent OnLogData;

        public Converter Converter { get; set; }

        Timer IsReady, WsReconnecter;

        // buckets
        private List<SpeckleLayer> BucketLayers = new List<SpeckleLayer>();
        private List<object> BucketObjects = new List<object>();
        private List<SpeckleObject> SpeckleBucketObjects = new List<SpeckleObject>();

        private Dictionary<string, SpeckleObject> ObjectCache = new Dictionary<string, SpeckleObject>();


        public SpeckleApiClient(string baseUrl, bool isPersistent = false) : base()
        {
            _settings = new System.Lazy<Newtonsoft.Json.JsonSerializerSettings>(() =>
            {
                var settings = new Newtonsoft.Json.JsonSerializerSettings();
                UpdateJsonSerializerSettings(settings);
                return settings;
            });

            UseGzip = true;

            BaseUrl = baseUrl;
            IsPersistent = isPersistent;

            SetReadyTimer();
        }

        public async Task IntializeReceiver(string streamId, string documentName, string documentType, string documentGuid, string authToken = null)
        {
            if (Role != null)
                throw new Exception("Role changes are not permitted. Maybe create a new client?");

            Role = ClientRole.Receiver;
            StreamId = streamId;

            try
            {
                AuthToken = authToken;
                User = (await this.UserGetProfileAsync()).User;

            }
            catch (SwaggerException e)
            {
                OnError?.Invoke(this, new SpeckleEventArgs() { EventName = e.StatusCode, EventData = e.Message });
            }

            try
            {
                Stream = (await this.StreamGetAsync(streamId)).Stream;
                await SetupClient(documentName, documentType, documentGuid);
                SetupWebsocket();
            }
            catch (SwaggerException e)
            {
                OnError?.Invoke(this, new SpeckleEventArgs() { EventName = e.StatusCode, EventData = e.Message });
            }


        }

        public async Task<string> IntializeSender(string authToken, string documentName, string documentType, string documentGuid)
        {
            if (Role != null)
                throw new Exception("Role changes are not permitted. Maybe create a new client?");

            Role = ClientRole.Sender;

            try
            {
                AuthToken = authToken;
                User = (await this.UserGetProfileAsync()).User;
            }
            catch (SwaggerException e)
            {
                OnError?.Invoke(this, new SpeckleEventArgs() { EventName = "error", EventData = "Could not log in: " + e.Message });
                return null;
            }

            try
            {
                Stream = (await this.StreamCreateAsync()).Stream;
                StreamId = Stream.StreamId;

                await SetupClient(documentName, documentType, documentGuid);
                SetupWebsocket();

                return Stream.StreamId;
            }
            catch (SwaggerException e)
            {
                OnError?.Invoke(this, new SpeckleEventArgs() { EventName = e.StatusCode, EventData = e.Message });

                return null;
            }

        }

        private async Task SetupClient(string documentName = null, string documentType = null, string documentGuid = null)
        {
            if (ClientId == null)
            {
                LogEvent("Creating a new client.");
                var payload = new PayloadClientCreate() { Client = new SpeckleClient() { StreamId = StreamId, Role = Role.ToString(), Online = true, DocumentGuid = documentGuid, DocumentName = documentName, DocumentType = documentType } };
                ClientId = (await this.ClientCreateAsync(payload)).ClientId;
            }
            else
            {
                LogEvent("Setting client to alive.");
                var payload = new PayloadClientUpdate() { Client = new SpeckleClient() { Online = true } };
                this.ClientUpdate(payload, ClientId);
            }
        }

        private void LogEvent(string what)
        {
            OnLogData?.Invoke(this, new SpeckleEventArgs() { EventData = what });
        }

        private void SetReadyTimer()
        {
            IsReady = new Timer(200) { AutoReset = false, Enabled = true };
            IsReady.Elapsed += (sender, e) =>
            {
                LogEvent("Checking readiness...");
                if (StreamId == null || ClientId == null || WsConnected == false) { IsReady.Start(); return; }
                OnReady?.Invoke(this, new SpeckleEventArgs() { EventName = "client-ready" });
                IsConnected = true;
                LogEvent("Client is ready!");
            };
        }

        private void SetWsReconnectTimer()
        {
            WsReconnecter = new Timer(1000) { AutoReset = false, Enabled = false };
            WsReconnecter.Elapsed += (sender, e) =>
            {
                if (IsDisposed) return;
                WebsocketClient.Connect();
            };
        }

        private void SetupWebsocket()
        {
            SetWsReconnectTimer();

            WebsocketClient = new WebSocket(BaseUrl.Replace("http", "ws") + "?access_token=" + AuthToken + "&stream_id=" + StreamId + "&client_id=" + ClientId);

            WebsocketClient.OnOpen += (sender, e) =>
            {
                WsConnected = true;
                WsReconnecter.Stop();
            };

            WebsocketClient.OnClose += (sender, e) =>
            {
                WsConnected = false;
                WsReconnecter.Start();
                OnError?.Invoke(this, new SpeckleEventArgs() { EventName = "websocket-disconnected" });
            };

            WebsocketClient.OnMessage += (sender, e) =>
            {
                if (e.Data == "ping") { WebsocketClient.Send("alive"); LogEvent("Got a ws ping."); return; }

                LogEvent("Got a ws message.");

                OnWsMessage?.Invoke(this, new SpeckleEventArgs() { EventName = "websocket-message", EventObject = JsonConvert.DeserializeObject<System.Dynamic.ExpandoObject>(e.Data), EventData = e.Data });
            };

            WebsocketClient.Connect();
        }

        public void SendMessage(string receipientId, dynamic args)
        {
            var eventData = new
            {
                eventName = "message",
                senderId = ClientId,
                recipientId = receipientId,
                streamId = StreamId,
                args = args
            };

            WebsocketClient.Send(JsonConvert.SerializeObject(eventData));
        }

        public void BroadcastMessage(dynamic args)
        {
            var eventData = new
            {
                eventName = "broadcast",
                senderId = ClientId,
                streamId = StreamId,
                args = args
            };

            WebsocketClient.Send(JsonConvert.SerializeObject(eventData));
        }

        public void LogError(SwaggerException err)
        {
            OnError?.Invoke(this, new SpeckleEventArgs() { EventName = err.StatusCode, EventData = err.Message, EventObject = err });
        }

        protected SpeckleApiClient(SerializationInfo info, StreamingContext context)
        {
            _settings = new System.Lazy<Newtonsoft.Json.JsonSerializerSettings>(() =>
            {
                var settings = new Newtonsoft.Json.JsonSerializerSettings();
                UpdateJsonSerializerSettings(settings);
                return settings;
            });

            UseGzip = true;

            BaseUrl = info.GetString("BaseUrl");
            StreamId = info.GetString("StreamId");
            Role = (ClientRole)info.GetInt32("Role");
            AuthToken = info.GetString("ApiToken");
            ClientId = info.GetString("ClientId");

            Stream = StreamGet(StreamId).Stream;

            // does not need waiting for, as we already have a clientid.
            SetupClient();
            SetupWebsocket();

            SetReadyTimer();
            SetWsReconnectTimer();
        }

        public void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            info.AddValue("BaseUrl", BaseUrl);
            info.AddValue("StreamId", StreamId);
            info.AddValue("Role", Role);
            info.AddValue("ApiToken", AuthToken);
            info.AddValue("ClientId", ClientId);
        }

        public void Dispose(bool delete = false)
        {
            IsDisposed = true;

            if (!delete)
            {
                var payload = new PayloadClientUpdate() { Client = new SpeckleClient() { Online = false } };
                ClientUpdateAsync(payload, ClientId);
                WebsocketClient?.Close();
                return;
            }

            this.ClientDeleteAsync(ClientId);
            WebsocketClient?.Close();
        }
    }
}
