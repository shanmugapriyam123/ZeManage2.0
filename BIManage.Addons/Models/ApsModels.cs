using System;

namespace BIManage.Addons.Models
{
    public class Hub
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Region { get; set; }
    }

    public class Project
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string HubId { get; set; }
        public string Region { get; set; }
    }

    public class Folder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProjectId { get; set; }
    }

    public class Item
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ProjectId { get; set; }
    }

    public class ApsVersion
    {
        public string Id { get; set; }
        public int VersionNumber { get; set; }
        public string Name { get; set; }
        public string Region { get; set; }
        public string ProjectGuidRaw { get; set; }
        public string ModelGuidRaw { get; set; }
    }
}
