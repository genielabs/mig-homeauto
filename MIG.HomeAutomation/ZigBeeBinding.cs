using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ZigBeeNet;
using ZigBeeNet.ZCL;
using ZigBeeNet.ZCL.Clusters;
using ZigBeeNet.ZCL.Clusters.General;
using ZigBeeNet.ZDO;
using ZigBeeNet.ZDO.Command;
using ZigBeeNet.ZDO.Field;

namespace MIG.Interfaces.HomeAutomation
{
    internal class ZigBeeBinding : ResponseStatus
    {
        // ZigBeeNet sends application-cluster traffic from local endpoint 1.
        public byte CoordinatorEndpoint => 1;
        public string Coordinator { get; }
        public byte Endpoint { get; }
        public ushort Cluster { get; }
        public bool Bound { get; private set; }
        public string Outcome { get; private set; }
        public string Reporting { get; private set; } = "Unknown";
        public ushort? MinimumInterval { get; private set; }
        public ushort? MaximumInterval { get; private set; }

        private readonly ZigBeeNode node;
        private readonly ZclCluster cluster;
        private readonly IeeeAddress coordinator;

        internal ZigBeeBinding(ZigBeeNode node, byte endpoint, ushort clusterId, IeeeAddress coordinator)
            : base(Status.Ok, "")
        {
            this.node = node;
            this.coordinator = coordinator;
            Coordinator = coordinator.ToString();
            Endpoint = endpoint;
            Cluster = clusterId;
            cluster = node.GetEndpoint(endpoint)?.GetInputCluster(clusterId)
                ?? throw new ArgumentException("The endpoint does not advertise this input cluster.");
        }

        internal static bool TryParseCluster(string value, out ushort id)
        {
            bool hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ushort.TryParse(hex ? value.Substring(2) : value,
                       hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out id)
                   && (id == ZclOnOffCluster.CLUSTER_ID || id == ZclLevelControlCluster.CLUSTER_ID);
        }

        internal async Task Inspect(bool ensure)
        {
            Bound = await ReadBound().ConfigureAwait(false);
            if (!ensure || Bound)
            {
                Outcome = Bound ? (ensure ? "AlreadyPresent" : "Present") : "Absent";
            }
            else
            {
                var result = await Add().ConfigureAwait(false);
                if (!(result.Response is BindResponse response) || response.Status != ZdoStatus.SUCCESS)
                    throw new InvalidOperationException($"Bind failed: {result.Response?.ToString() ?? "no response"}");
                Bound = await ReadBound().ConfigureAwait(false);
                if (!Bound) throw new InvalidOperationException("Bind acknowledged but missing from the device binding table.");
                Outcome = "Added";
            }

            // Reporting is independent of the binding. Some devices cannot read it back.
            try
            {
                var result = await ReadReporting().ConfigureAwait(false);
                var record = (result.Response as ReadReportingConfigurationResponse)?.Records?
                    .FirstOrDefault(r => r.Direction == 0 && r.AttributeIdentifier == 0);
                switch (record?.Status)
                {
                    case ZclStatus.SUCCESS:
                        MinimumInterval = record.MinimumReportingInterval;
                        MaximumInterval = record.MaximumReportingInterval;
                        Reporting = record.MaximumReportingInterval == ushort.MaxValue ? "Disabled" : "Configured";
                        break;
                    case ZclStatus.NOT_FOUND:
                        Reporting = "NotConfigured";
                        break;
                    case ZclStatus.UNSUPPORTED_ATTRIBUTE:
                    case ZclStatus.UNREPORTABLE_ATTRIBUTE:
                        Reporting = "Unsupported";
                        break;
                }
            }
            catch (Exception e)
            {
                MigService.Log.Debug($"Could not read ZigBee reporting configuration: {e.Message}");
            }
        }

        private async Task<bool> ReadBound()
        {
            int index = 0;
            do
            {
                var result = await ReadPage((byte)index).ConfigureAwait(false);
                if (!(result.Response is ManagementBindResponse page) || page.Status != ZdoStatus.SUCCESS)
                    throw new InvalidOperationException($"Binding table read failed: {result.Response?.ToString() ?? "no response"}");
                var entries = page.BindingTableList;
                if (entries == null || page.StartIndex != index || index + entries.Count > page.BindingTableEntries)
                    throw new InvalidOperationException("Invalid binding table page.");
                if (entries.Any(Matches)) return true;
                index += entries.Count;
                if (index == page.BindingTableEntries) return false;
                if (entries.Count == 0) throw new InvalidOperationException("Incomplete binding table.");
            } while (index <= byte.MaxValue);
            throw new InvalidOperationException("Invalid binding table size.");
        }

        private bool Matches(BindingTable b) =>
            node.IeeeAddress.Equals(b.SrcAddr) && b.SrcEndpoint == Endpoint && b.ClusterId == Cluster &&
            b.DstAddrMode == 3 && coordinator.Equals(b.DstAddr) && b.DstNodeEndpoint == CoordinatorEndpoint;

        internal virtual Task<CommandResult> ReadPage(byte index)
        {
            var request = new ManagementBindRequest { DestinationAddress = new ZigBeeEndpointAddress(node.NetworkAddress), StartIndex = index };
            return node.SendTransaction(request, request);
        }

        internal virtual Task<CommandResult> Add() => cluster.Bind(coordinator, CoordinatorEndpoint);
        internal virtual Task<CommandResult> ReadReporting() => cluster.GetReporting(0);

        internal static bool IsLoadEndpoint(ushort profile, ushort device)
        {
            // HA outputs, outlets, smart plugs, lamps and ballasts; ZLL lamps and plug-in units.
            switch (profile)
            {
                case 0x0104:
                    return device == 0x0002 || device == 0x0003 || device == 0x0009 || device == 0x0051 ||
                        (device >= 0x0100 && device <= 0x0102) || (device >= 0x0108 && device <= 0x010D);
                case 0xC05E:
                    return device == 0x0000 || device == 0x0010 || device == 0x0100 || device == 0x0110 ||
                        device == 0x0200 || device == 0x0210 || device == 0x0220;
                default:
                    return false;
            }
        }
    }
}
