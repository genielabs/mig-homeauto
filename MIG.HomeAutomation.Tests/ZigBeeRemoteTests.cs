using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MIG.Interfaces.HomeAutomation;
using NUnit.Framework;
using ZigBeeNet;
using ZigBeeNet.Transport;
using ZigbeeNet.Hardware.ConBee;

namespace MIG.HomeAutomation.Tests;

public class ZigBeeRemoteTests
{
    private const string Remote = "00178801087005A3";

    private sealed class Driver : ZigBee
    {
        public readonly BlockingCollection<(string Source, string Path, object Value)> Events = new();
        public Driver(ZigBeeNetworkManager manager) : base(manager) { }

        protected override void OnInterfacePropertyChanged(string domain, string source, string description, string path, object value)
            => Events.Add((source, path, value));
    }

    [TestCase(0x0000, 0x0013, 0x01, 0xFFFD)] // ZDO device announcement
    [TestCase(0x0104, 0x0008, 0x18, 0x3BAC)] // attribute report
    [TestCase(0x0104, 0x0006, 0x05, 0x3BAC)] // manufacturer-specific
    [TestCase(0x0104, 0x0006, 0x01, 0x0000)] // coordinator unicast
    public void EndpointZeroIsPreservedForUnrelatedTraffic(int profile, int cluster, int control, int destination)
    {
        var manager = new ConBeeNetworkManager(new ZigbeeDongleConBee(null));
        var frame = new ZigBeeApsFrame { Profile = (ushort)profile, Cluster = (ushort)cluster, DestinationAddress = (ushort)destination,
            DestinationEndpoint = 0, Payload = new byte[] { (byte)control, 2, 1 } };
        ((IZigBeeTransportReceive)manager).ReceiveCommand(frame);
        Assert.That(frame.DestinationEndpoint, Is.Zero);
    }

    [Test]
    public void Rwl021GroupCommandsProduceOneActionPerCapturedFrame()
    {
        var manager = new ConBeeNetworkManager(new ZigbeeDongleConBee(null));
        var driver = new Driver(manager);
        var remote = new ZigBeeNode(manager, new IeeeAddress(Remote), 0x062F);
        var endpoint = new ZigBeeEndpoint(remote, 1);
        endpoint.SetOutputClusterIds(new List<ushort> { 0x0006, 0x0008 });
        remote.AddEndpoint(endpoint);
        remote.SetNodeState(ZigBeeNodeState.ONLINE);
        manager.UpdateNode(remote);
        manager.AddCommandListener(new ZigBeeCommandListener(driver));
        manager.NetworkState = ZigBeeNetworkState.ONLINE;

        // ZCL payloads from the eight RWL021 gestures, in capture order. The ConBee
        // delivered each to group 0x3BAC with destination endpoint zero.
        var capture = new (ushort Cluster, string Payload, string Action)[]
        {
            (6, "010201", "On"),
            (8, "010302001E0900", "BrightnessUp"),
            (8, "010402011E0900", "BrightnessDown"),
            (6, "0105400000", "Off"),
            (6, "010601", "On"),
            (8, "010702001E0900", "BrightnessUp"),
            (8, "01080200380900", "BrightnessUp"),
            (8, "01090200380900", "BrightnessUp"),
            (8, "010A0200380900", "BrightnessUp"),
            (8, "010B0200380900", "BrightnessUp"),
            (8, "010C03", "BrightnessStop"),
            (8, "010D02011E0900", "BrightnessDown"),
            (8, "010E0201380900", "BrightnessDown"),
            (8, "010F0201380900", "BrightnessDown"),
            (8, "01100201380900", "BrightnessDown"),
            (8, "01110201380900", "BrightnessDown"),
            (8, "011203", "BrightnessStop"),
            (6, "0113400000", "Off"),
        };
        foreach (var (cluster, payload, action) in capture)
        {
            var frame = new ZigBeeApsFrame
            {
                Profile = 0x0104, Cluster = cluster, SourceAddress = 0x062F, SourceEndpoint = 1,
                DestinationAddress = 0x3BAC, DestinationEndpoint = 0, Payload = Convert.FromHexString(payload)
            };
            ((IZigBeeTransportReceive)manager).ReceiveCommand(frame);
            Assert.That(driver.Events.TryTake(out var received, TimeSpan.FromSeconds(2)), Is.True, payload);
            Assert.That(received, Is.EqualTo((Remote, "Receiver.Action", (object)action)), payload);
        }
        Assert.That(driver.Events.Count, Is.Zero);
    }
}
