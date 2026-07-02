using System;
using System.Collections.Generic;
using System.Net;
using BIManage.Addons.Models;
using Newtonsoft.Json.Linq;

namespace BIManage.Addons.Services
{
    /// <summary>
    /// APS (Autodesk Platform Services) REST API client.
    /// Provides access to Hubs, Projects, Folders, Items, and Versions.
    /// </summary>
    public static class DataService
    {
        private const string BaseUrl = "https://developer.api.autodesk.com";

        private static WebClient NewClient()
        {
            var c = new WebClient();
            c.Headers[HttpRequestHeader.Authorization] = "Bearer " + AuthService.AccessToken;
            c.Headers[HttpRequestHeader.Accept] = "application/json";
            return c;
        }

        public static List<Hub> GetHubs()
        {
            var hubs = new List<Hub>();
            using var client = NewClient();
            JObject root = JObject.Parse(client.DownloadString($"{BaseUrl}/project/v1/hubs"));
            foreach (JToken h in root["data"] as JArray ?? new JArray())
                hubs.Add(new Hub
                {
                    Id = (string)h["id"],
                    Name = (string)h["attributes"]?["name"],
                    Region = (string)h["attributes"]?["region"]
                });
            return hubs;
        }

        public static List<Project> GetProjects(string hubId)
        {
            var list = new List<Project>();
            using var client = NewClient();
            JObject root = JObject.Parse(
                client.DownloadString($"{BaseUrl}/project/v1/hubs/{hubId}/projects"));

            string hubRegion = (string)root["jsonapi"]?["region"] ?? "US";
            foreach (JToken p in root["data"] as JArray ?? new JArray())
            {
                string id = (string)p["id"];
                string nm = (string)p["attributes"]?["name"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(nm)) continue;
                if (!id.StartsWith("b.") && !id.StartsWith("a.")) id = "b." + id;
                list.Add(new Project { Id = id, Name = nm, HubId = hubId, Region = hubRegion });
            }
            return list;
        }

        public static List<Folder> GetTopFolders(string hubId, string projectId)
        {
            var list = new List<Folder>();
            using var client = NewClient();
            JObject root = JObject.Parse(
                client.DownloadString($"{BaseUrl}/project/v1/hubs/{hubId}/projects/{projectId}/topFolders"));
            foreach (JToken f in root["data"] as JArray ?? new JArray())
            {
                string id = (string)f["id"];
                string nm = (string)f["attributes"]?["displayName"] ??
                            (string)f["attributes"]?["name"];
                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(nm))
                    list.Add(new Folder { Id = id, Name = nm, ProjectId = projectId });
            }
            return list;
        }

        public static void GetFolderContents(string projectId, string folderId,
            out List<Folder> folders, out List<Item> items)
        {
            folders = new List<Folder>();
            items = new List<Item>();

            using var client = NewClient();
            JObject root = JObject.Parse(
                client.DownloadString($"{BaseUrl}/data/v1/projects/{projectId}/folders/{folderId}/contents"));

            var nameMap = new Dictionary<string, string>();
            foreach (JToken inc in root["included"] as JArray ?? new JArray())
            {
                if ((string)inc["type"] == "versions")
                {
                    string itemId = (string)inc["relationships"]?["item"]?["data"]?["id"];
                    string nm = (string)inc["attributes"]?["displayName"];
                    if (!string.IsNullOrEmpty(itemId) && !string.IsNullOrEmpty(nm))
                        nameMap[itemId] = nm;
                }
            }

            foreach (JToken t in root["data"] as JArray ?? new JArray())
            {
                string type = (string)t["type"];
                string id = (string)t["id"];
                if (type == "folders")
                {
                    string nm = (string)t["attributes"]?["displayName"] ??
                                (string)t["attributes"]?["name"];
                    if (!string.IsNullOrEmpty(nm))
                        folders.Add(new Folder { Id = id, Name = nm, ProjectId = projectId });
                }
                else if (type == "items")
                {
                    string nm = (string)t["attributes"]?["displayName"];
                    if (string.IsNullOrEmpty(nm) && nameMap.TryGetValue(id, out var nm2)) nm = nm2;
                    if (string.IsNullOrEmpty(nm)) nm = id;
                    items.Add(new Item { Id = id, Name = nm, ProjectId = projectId });
                }
            }
        }

        public static List<ApsVersion> GetItemVersions(string projectId, string itemId)
        {
            var vers = new List<ApsVersion>();
            using var client = NewClient();
            JObject root = JObject.Parse(
                client.DownloadString($"{BaseUrl}/data/v1/projects/{projectId}/items/{itemId}/versions"));

            foreach (JToken v in root["data"] as JArray ?? new JArray())
            {
                var ver = new ApsVersion
                {
                    Id = (string)v["id"],
                    VersionNumber = v["attributes"]?["versionNumber"]?.ToObject<int>() ?? -1,
                    Name = (string)v["attributes"]?["name"]
                };
                var ext = v["attributes"]?["extension"];
                if ((string)ext?["type"] == "versions:autodesk.bim360:C4RModel")
                {
                    ver.ProjectGuidRaw = (string)ext["data"]?["projectGuid"];
                    ver.ModelGuidRaw = (string)ext["data"]?["modelGuid"];
                }
                vers.Add(ver);
            }
            vers.Sort((a, b) => a.VersionNumber.CompareTo(b.VersionNumber));
            return vers;
        }
    }
}
