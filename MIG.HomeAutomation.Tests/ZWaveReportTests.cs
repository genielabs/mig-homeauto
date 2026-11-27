using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ZWaveLib;
using ZWaveLib.CommandClasses;
using Driver = MIG.Interfaces.HomeAutomation.ZWave;

namespace MIG.HomeAutomation.Tests;

public class ZWaveReportTests
{
    private sealed class TestController : ZWaveController
    {
        public void Receive(NodeEvent report) => OnNodeUpdated(new NodeUpdatedEventArgs(report.Node.Id, report));
    }

    private sealed class TestDriver : Driver
    {
        public readonly Dictionary<string, object> Properties = new Dictionary<string, object>();
        public TestDriver(TestController controller) : base(controller) { }

        protected override void OnInterfacePropertyChanged(string domain, string source, string description, string path, object value)
        {
            Assert.That(source, Is.EqualTo("12"));
            Properties[path] = value;
        }
    }

    [Test]
    public void CapabilitiesArePublishedSeparatelyFromReadingsAndFromOtherNotificationTypes()
    {
        using var controller = new TestController();
        var driver = new TestDriver(controller);
        var node = new ZWaveNode(controller, 12);
        controller.Receive(new Meter().GetEvent(node, new byte[] { 0x32, 4, 0x81, 5 }));
        controller.Receive(new Meter().GetEvent(node, new byte[] { 0x32, 2, 1, 0x11, 42 }));
        var alarm = new Alarm();
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 8, 1, 0xa0 }));
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 2, 7, 2, 8, 1 }));
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 2, 5, 1, 4 }));
        var meter = JObject.Parse((string)driver.Properties["ZWaveNode.Meter.SupportedReport"]);
        Assert.That(meter["Scales"]!.Values<int>(), Is.EqualTo(new[] { 0, 2 }));
        Assert.That(driver.Properties["Meter.Watts"], Is.EqualTo(42));
        var types = JObject.Parse((string)driver.Properties["ZWaveNode.Notification.SupportedReport"]);
        Assert.That(types["Types"]!.Values<int>(), Is.EqualTo(new[] { 5, 7 }));
        Assert.That(driver.Properties["ZWaveNode.Notification.SupportedEvents.7"], Is.EqualTo("[3,8]"));
        Assert.That(driver.Properties["ZWaveNode.Notification.SupportedEvents.5"], Is.EqualTo("[2]"));
    }

    [Test]
    public void ClearingMotionLeavesTheTamperFieldActive()
    {
        using var controller = new TestController();
        var driver = new TestDriver(controller);
        var node = new ZWaveNode(controller, 12);
        var alarm = new Alarm();
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 5, 0, 0, 0, 0xff, 7, 8, 0 }));
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 5, 0, 0, 0, 0xff, 7, 3, 0 }));
        controller.Receive(alarm.GetEvent(node, new byte[] { 0x71, 5, 0, 0, 0, 0xff, 7, 0, 1, 8 }));
        Assert.That(driver.Properties["Sensor.MotionDetect"], Is.EqualTo((byte)0));
        Assert.That(driver.Properties["Sensor.Tamper"], Is.EqualTo((byte)0xff));
    }
}
