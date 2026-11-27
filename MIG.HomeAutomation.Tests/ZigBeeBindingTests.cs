using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using MIG.Interfaces.HomeAutomation;
using NUnit.Framework;
using ZigBeeNet;
using ZigBeeNet.Serialization;
using ZigBeeNet.Transport;
using ZigbeeNet.Hardware.ConBee;
using ZigBeeNet.ZCL;
using ZigBeeNet.ZCL.Clusters.General;
using ZigBeeNet.ZCL.Field;
using ZigBeeNet.ZDO;
using ZigBeeNet.ZDO.Command;
using ZigBeeNet.ZDO.Field;

namespace MIG.HomeAutomation.Tests;

public class ZigBeeBindingTests
{
    private static readonly IeeeAddress Lamp = new("CCCCCCFFFED460E1");
    private static readonly IeeeAddress Coordinator = new("00212EFFFF0597A0");
    private static ZigBeeNode NewNode(IeeeAddress address)
    {
        var node = new ZigBeeNode(new ConBeeNetworkManager(new ZigbeeDongleConBee(null)), address, 0xDAF2);
        var ep = new ZigBeeEndpoint(node, 1);
        ep.SetInputClusterIds(new List<ushort> { 6, 8 });
        node.AddEndpoint(ep);
        return node;
    }

    // Binding records from the Studio Ambient management table, in ZigBee wire order.
    private static BindingTable Entry(string hex)
    {
        var entry = new BindingTable();
        entry.Deserialize(new DefaultDeserializer(Convert.FromHexString(hex)));
        return entry;
    }

    private sealed class Device : ZigBeeBinding
    {
        internal readonly List<BindingTable> Entries = new()
        {
            Entry("E160D4FEFFCCCCCC010020014F75"),
            Entry("E160D4FEFFCCCCCC01002001AC3B")
        };
        internal int Writes;
        internal bool Timeout, IgnoreBind, RefuseBind, ReportingUnavailable;
        internal Device() : base(NewNode(Lamp), 1, 6, CoordinatorAddress) { }
        private static IeeeAddress CoordinatorAddress => ZigBeeBindingTests.Coordinator;

        internal override Task<CommandResult> ReadPage(byte index)
        {
            if (Timeout) return Task.FromResult(new CommandResult());
            return Task.FromResult(new CommandResult(new ManagementBindResponse {
                Status = ZdoStatus.SUCCESS, StartIndex = index, BindingTableEntries = (byte)Entries.Count,
                BindingTableList = Entries.GetRange(index, Math.Min(1, Entries.Count - index))
            }));
        }
        internal override Task<CommandResult> Add()
        {
            Writes++;
            if (!IgnoreBind && !RefuseBind) Entries.Add(Entry("E160D4FEFFCCCCCC01060003A09705FFFF2E210001"));
            return Task.FromResult(new CommandResult(new BindResponse { Status = RefuseBind ? ZdoStatus.NOT_SUPPORTED : ZdoStatus.SUCCESS }));
        }
        internal override Task<CommandResult> ReadReporting() => Task.FromResult(ReportingUnavailable ? new CommandResult() :
            new CommandResult(new ReadReportingConfigurationResponse { Records = new List<AttributeReportingStatusRecord> {
                new() { AttributeIdentifier = 0, Direction = 0, Status = ZclStatus.SUCCESS, MinimumReportingInterval = 1, MaximumReportingInterval = 65534 }
            }}));
    }

    [Test]
    public async Task ReadAndEnsurePreserveGroupsAndVerifyExactUnicastEntryAcrossPages()
    {
        var device = new Device();
        device.Entries.Add(Entry("E160D4FEFFCCCCCC01060003A09705FFFF2E210002"));
        var original = device.Entries.ToArray();
        await device.Inspect(false);
        Assert.That(device.Outcome, Is.EqualTo("Absent"));
        Assert.That(device.Writes, Is.Zero);
        Assert.That(device.Reporting, Is.EqualTo("Configured"));
        Assert.That(device.MaximumInterval, Is.EqualTo(65534));
        await device.Inspect(true);
        Assert.That(device.Outcome, Is.EqualTo("Added"));
        Assert.That(device.Bound, Is.True);
        await device.Inspect(true);
        Assert.That(device.Outcome, Is.EqualTo("AlreadyPresent"));
        Assert.That(device.Writes, Is.EqualTo(1));
        Assert.That(device.Entries.Count, Is.EqualTo(original.Length + 1));
        for (int i = 0; i < original.Length; i++) Assert.That(device.Entries[i], Is.SameAs(original[i]));
    }

    [TestCase(true, false, false, 0)]
    [TestCase(false, true, false, 1)]
    [TestCase(false, false, true, 1)]
    public void ReadTimeoutRefusalAndMissingReadbackDoNotProduceSuccess(bool timeout, bool ignore, bool refuse, int writes)
    {
        var device = new Device { Timeout = timeout, IgnoreBind = ignore, RefuseBind = refuse };
        Assert.ThrowsAsync<InvalidOperationException>(() => device.Inspect(true));
        Assert.That(device.Writes, Is.EqualTo(writes));
    }

    [Test]
    public async Task UnverifiedReportingIsIndependentOfVerifiedBinding()
    {
        var device = new Device { ReportingUnavailable = true };
        await device.Inspect(true);
        Assert.That(device.Bound, Is.True);
        Assert.That(device.Reporting, Is.EqualTo("Unknown"));
    }

    [TestCase(0x0104, 0x0051, true)] // metering smart plug
    [TestCase(0x0104, 0x0101, true)] // dimmable lamp
    [TestCase(0xC05E, 0x0000, true)] // ZLL lamp
    [TestCase(0x0104, 0x0000, false)] // HA controller with the same device ID
    [TestCase(0x0104, 0x0106, false)] // illuminance sensor
    [TestCase(0xC05E, 0x0820, false)] // remote
    public void OnlyLoadDescriptorsAreEligibleForAutomaticBinding(int profile, int device, bool eligible)
        => Assert.That(ZigBeeBinding.IsLoadEndpoint((ushort)profile, (ushort)device), Is.EqualTo(eligible));

    [Test]
    public void InclusionIgnoresKnownNodesAndWaitsForNewNodeDescriptors()
    {
        var inclusion = new ZigBee.Inclusion(new[] { Lamp.ToString() });
        var old = NewNode(Lamp);
        old.NodeDescriptor = new NodeDescriptor(0, 64, 0, false, 0, (byte)NodeDescriptor.LogicalType.ROUTER, 0, 64, false, 0);
        inclusion.Observe(old);
        var added = NewNode(new IeeeAddress("0123456789ABCDEF"));
        inclusion.Observe(added);
        Assert.That(inclusion.Completed.Task.IsCompleted, Is.False);
        added.NodeDescriptor = old.NodeDescriptor;
        inclusion.Observe(added);
        Assert.That(inclusion.Completed.Task.IsCompleted, Is.True);
        Assert.That(inclusion.Completed.Task.Result, Is.SameAs(added));
    }

    private sealed class TestTransport : ZigbeeDongleConBee, IZigBeeTransportTransmit
    {
        internal readonly BlockingCollection<ZigBeeApsFrame> Sent = new();
        internal TestTransport() : base(null) { }
        void IZigBeeTransportTransmit.SendCommand(ZigBeeApsFrame frame) => Sent.Add(frame);
    }

    private sealed class InclusionDriver : ZigBee
    {
        internal readonly BlockingCollection<string> Events = new();
        internal InclusionDriver(ZigBeeNetworkManager manager) : base(manager) { }
        protected override void OnInterfacePropertyChanged(string domain, string source, string description, string path, object value)
            => Events.Add(path);
    }

    [Test]
    public async Task NodeAddClosesJoiningAndAttemptsBothDimmerBindingsEvenWhenDeviceRefusesReads()
    {
        var radio = new TestTransport();
        var manager = new ConBeeNetworkManager(radio);
        var driver = new InclusionDriver(manager);
        manager.UpdateNode(new ZigBeeNode(manager, Coordinator, 0));
        var known = new ZigBeeNode(manager, new IeeeAddress("0123456789ABCDEF"), 0x1234);
        known.NodeDescriptor = new NodeDescriptor(0, 64, 0, false, 0, 1, 0, 64, false, 0);
        manager.UpdateNode(known);
        manager.NetworkState = ZigBeeNetworkState.ONLINE;
        var timeout = TimeSpan.FromSeconds(5);
        var adding = Task.Run(() => (ResponseStatus)driver.InterfaceControl(
            new MigInterfaceCommand("HomeAutomation.ZigBee/0/Controller.NodeAdd")));
        Assert.That(radio.Sent.TryTake(out var open, timeout), Is.True);
        Assert.That(open!.Cluster, Is.EqualTo(0x0036));
        driver.UpdateNode(known);
        Assert.That(adding.IsCompleted, Is.False);

        var lamp = new ZigBeeNode(manager, Lamp, 0xDAF2) { NodeDescriptor = known.NodeDescriptor };
        var ep = new ZigBeeEndpoint(lamp, 1) { ProfileId = 0x0104, DeviceId = 0x0101 };
        ep.SetInputClusterIds(new List<ushort> { 6, 8 });
        lamp.AddEndpoint(ep);
        manager.UpdateNode(lamp);
        driver.UpdateNode(lamp);
        Assert.That((await adding.WaitAsync(timeout)).Message, Is.EqualTo(Lamp.ToString()));
        Assert.That(radio.Sent.TryTake(out var close, timeout), Is.True);
        Assert.That(close!.Cluster, Is.EqualTo(0x0036));
        Assert.That(close.Payload[1], Is.Zero);

        var paths = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            Assert.That(radio.Sent.TryTake(out var read, timeout), Is.True);
            Assert.That(read!.Cluster, Is.EqualTo(0x0033));
            Assert.That(read.DestinationAddress, Is.EqualTo(0xDAF2));
            manager.ReceiveCommand(new ZigBeeApsFrame {
                SourceAddress = 0xDAF2, SourceEndpoint = 0, DestinationEndpoint = 0, Profile = 0, Cluster = 0x8033,
                Payload = new byte[] { read.Payload[0], (byte)ZdoStatus.NOT_SUPPORTED }
            });
            Assert.That(driver.Events.TryTake(out var path, timeout), Is.True);
            paths.Add(path!);
        }
        Assert.That(paths, Is.EquivalentTo(new[] { "ZigBeeNode.Binding.1.0006", "ZigBeeNode.Binding.1.0008" }));
        driver.UpdateNode(lamp);
        Assert.That(radio.Sent.Count, Is.Zero);
    }
}
