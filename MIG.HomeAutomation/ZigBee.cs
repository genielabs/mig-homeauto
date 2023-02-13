using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MIG.Config;
using MIG.Interfaces.HomeAutomation.Commons;
using ZigBeeNet;
using ZigBeeNet.App.Basic;
using ZigBeeNet.App.Discovery;
using ZigBeeNet.App.IasClient;
using ZigBeeNet.DataStore.Json;
using ZigbeeNet.Hardware.ConBee;
using ZigBeeNet.Hardware.Digi.XBee;
using ZigBeeNet.Hardware.Ember;
using ZigBeeNet.Hardware.TI.CC2531;
using ZigBeeNet.Tranport.SerialPort;
using ZigBeeNet.Transaction;
using ZigBeeNet.Transport;
using ZigBeeNet.Util;
using ZigBeeNet.ZCL;
using ZigBeeNet.ZCL.Clusters;
using ZigBeeNet.ZCL.Clusters.ColorControl;
using ZigBeeNet.ZCL.Clusters.General;
using ZigBeeNet.ZCL.Clusters.IasZone;
using ZigBeeNet.ZCL.Clusters.LevelControl;
using ZigBeeNet.ZCL.Clusters.OnOff;
using ZigBeeNet.ZDO.Command;
using ZigBeeNet.ZDO.Field;

namespace MIG.Interfaces.HomeAutomation
{

    public class ZigBee : MigInterface
    {

        #region MigInterface API commands and events
        
        public enum Commands
        {
            NotSet,

            Controller_Discovery,
            Controller_NodeAdd,
            Controller_NodeRemove,
            Controller_SoftReset,
            Controller_HardReset,
            Controller_HealNetwork,
            Controller_NodeNeighborUpdate,
            Controller_NodeRoutingInfo,
            
            ManufacturerName_Get,
            ModelIdentifier_Get,
            NodeInfo_Get,

            Basic_Get,
            Basic_Set,

            Control_On,
            Control_Off,
            Control_Toggle,
            Control_Level,
            Control_ColorHsb,

            Thermostat_ModeGet,
            Thermostat_ModeSet,
            Thermostat_SetPointGet,
            Thermostat_SetPointSet,
            Thermostat_FanModeGet,
            Thermostat_FanModeSet,
            Thermostat_FanStateGet,
            Thermostat_OperatingStateGet,

            DoorLock_Set,
            DoorLock_Get
        }

        // ZigBee specific events
        const string EventPath_WakeUpInterval
            = "ZigBeeNode.WakeUpInterval";
        const string EventPath_Battery
            = "ZigBeeNode.Battery";
        const string EventPath_NodeInfo
            = "ZigBeeNode.NodeInfo";
        const string EventPath_RoutingInfo
            = "ZigBeeNode.RoutingInfo";
        const string EventPath_ManufacturerName
            = "ZigBeeNode.ManufacturerName";
        const string EventPath_ModelIdentifier
            = "ZigBeeNode.ModelIdentifier";
        const string EventPath_VersionReport
            = "ZigBeeNode.VersionReport";

        #endregion
        
        #region Private fields

        private const int NumberOfAddAttempts = 60;
        private const int NumberOfRemoveAttempts = 60;
        private const int DelayBetweenAttempts = 500;

        private ZigBeeNetworkManager networkManager;
        private ZigBeeSerialPort zigbeePort;
        private IZigBeeTransportTransmit transportTransmit;

        private string lastRemovedNode;
        private string lastAddedNode;

        #endregion
        
        #region MIG Interface members

        public event InterfaceModulesChangedEventHandler InterfaceModulesChanged;
        public event InterfacePropertyChangedEventHandler InterfacePropertyChanged;

        public bool IsEnabled { get; set; }

        public List<Option> Options { get; set; }

        public void OnSetOption(Option option)
        {
            if (IsEnabled)
                Connect();
        }

        List<InterfaceModule> modules = new List<InterfaceModule>();
        public List<InterfaceModule> GetModules()
        {
            return modules;
        }

        public bool IsConnected
        {
            get
            {
                return networkManager != null && networkManager.NetworkState == ZigBeeNetworkState.ONLINE;
            }
        }

        public object InterfaceControl(MigInterfaceCommand request)
        {
            var returnValue = new ResponseStatus(Status.Ok);
            bool raiseEvent = false;
            string eventParameter = ModuleEvents.Status_Level;
            string eventValue = "";

            // Check if controller is ready
            if (!IsConnected)
            {
                return new ResponseStatus(Status.Error, "Controller not connected.");
            }

            Commands command;
            Enum.TryParse(request.Command.Replace(".", "_"), out command);
            var node = networkManager.GetNode(new IeeeAddress(request.Address));
            
            // controller commands

            if (request.Address == "0") // "0" = controller
            {

                switch (command)
                {
                    case Commands.Controller_NodeAdd:
                        lastAddedNode = "";
                        // Enable node pairing
                        ZigBeeNode coord = networkManager.GetNode(0);
                        coord.PermitJoin(true);
                        for (int i = 0; i < NumberOfAddAttempts; i++)
                        {
                            if (!String.IsNullOrEmpty(lastAddedNode))
                            {
                                break;
                            }

                            Thread.Sleep(DelayBetweenAttempts);
                        }
                        coord.PermitJoin(false);
                        returnValue = new ResponseStatus(Status.Ok, lastAddedNode);
                        break;
                    case Commands.Controller_NodeRemove:
                        var nodeId = request.GetOption(0);
                        var nodeToRemove = networkManager.GetNode(new IeeeAddress(nodeId));
                        if (nodeToRemove != null && nodeToRemove.NetworkAddress > 0)
                        {
                            networkManager
                                .Leave(nodeToRemove.NetworkAddress, nodeToRemove.IeeeAddress, true);
                            networkManager.RemoveNode(nodeToRemove);
                            returnValue = new ResponseStatus(Status.Ok, $"Removed node {nodeToRemove.IeeeAddress}.");
                        }
                        else
                        {
                            returnValue = new ResponseStatus(Status.Error, $"Unknown node {nodeId}.");
                        }
                        break;
                    case Commands.Controller_SoftReset:
                        SoftReset();
                        break;
                    default:
                        returnValue = new ResponseStatus(Status.Error, "Command not understood.");
                        break;
                }
                return returnValue;
            }
            
            // node commands

            var module = modules.Find((m) => m.Address == request.Address);
            if (module == null)
            {
                return new ResponseStatus(Status.Error, $"Unknown node {request.Address}.");
            }
            var nodeData = module.CustomData as ZigBeeNodeData;

            if (node == null)
            {
                return new ResponseStatus(Status.Error, "Could not get node instance.");
            }
            // Get node endpoint
            var endpoint = node.GetEndpoints().FirstOrDefault();
            if (endpoint == null)
            {
                return new ResponseStatus(Status.Error, "Could not determine node endpoint address.");
            }
            ZigBeeEndpointAddress endpointAddress = endpoint.GetEndpointAddress();

            switch (command)
            {
                case Commands.Basic_Get:
                    ReadClusterLevelData(node, endpoint);
                    break;
                case Commands.Control_On:
                    networkManager
                        .Send(endpointAddress, new OnCommand());
                    nodeData.Level = nodeData.LastLevel > 0 ? nodeData.LastLevel : 1;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;

                case Commands.Control_Off:
                    networkManager
                        .Send(endpointAddress, new OffCommand());
                    nodeData.Level = 0;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;

                case Commands.Control_Toggle:
                    networkManager
                        .Send(endpointAddress, new ToggleCommand());
                    nodeData.Level = nodeData.Level > 0 ? 0 : nodeData.LastLevel;
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;

                case Commands.Control_Level:
                    var level = int.Parse(request.GetOption(0));
                    nodeData.Level = (level / 100D);
                    SetLevel(endpointAddress, level, nodeData.Transition);
                    eventValue = nodeData.Level.ToString(CultureInfo.InvariantCulture);
                    raiseEvent = true;
                    break;

                case Commands.Control_ColorHsb:
                    string color = request.GetOption(0);
                    string[] values = color.Split(',');
                    ushort transition = 4; // 400ms default transition
                    if (values.Length > 3)
                    {
                        transition = (ushort)(double.Parse(values[3], CultureInfo.InvariantCulture) * 10);
                    }
                    nodeData.Transition = transition;
                    double brightness = double.Parse(values[2], CultureInfo.InvariantCulture);
                    if (nodeData.Level != brightness)
                    {
                        nodeData.Level = brightness;
                        SetLevel(endpointAddress, (int)(brightness * 100), transition);
                    }
                    SetColorHsb
                    (
                        endpointAddress,
                        double.Parse(values[0], CultureInfo.InvariantCulture) * 360,
                        double.Parse(values[1], CultureInfo.InvariantCulture),
                        brightness, // status.level
                        transition
                    );
                    OnInterfacePropertyChanged(
                        this.GetDomain(),
                        node.IeeeAddress.ToString(),
                        "ZigBee Node",
                        eventParameter, // Status.Level
                        brightness.ToString(CultureInfo.InvariantCulture)
                    );
                    eventParameter = ModuleEvents.Status_ColorHsb;
                    eventValue = color;
                    raiseEvent = true;
                    break;
                case Commands.NodeInfo_Get:
                    ReadClusterData(node);
                    break;
                default:
                    returnValue = new ResponseStatus(Status.Error, "Command not understood.");
                    break;
            }

            if (raiseEvent)
            {
                OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", eventParameter, eventValue);
            }
            return returnValue;
        }

        public bool Connect()
        {
            Disconnect();
            string portName = this.GetOption("Port").Value;
            if (String.IsNullOrEmpty(portName))
            {
                return false;
            }
            zigbeePort = new ZigBeeSerialPort(portName, 115200);

            string driverName = this.GetOption("Driver").Value;
            if (String.IsNullOrEmpty(driverName))
            {
                return false;
            }
            
            switch (driverName)
            {
                case "cc2531":
                    transportTransmit = new ZigBeeDongleTiCc2531(zigbeePort); 
                    break;
                case "xbee":
                    transportTransmit = new ZigBeeDongleXBee(zigbeePort);
                    break;
                case "ember":
                    transportTransmit = new ZigBeeDongleEzsp(zigbeePort);
                    break;
                default: // "conbee"
                    transportTransmit = new ZigbeeDongleConBee(zigbeePort); 
                    break;
            }
            networkManager = new ZigBeeNetworkManager(transportTransmit);

            var dataStore = new JsonNetworkDataStore(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mig", "zigbee")
            );
            networkManager.SetNetworkDataStore(dataStore);
            // Add discovery extension
            ZigBeeDiscoveryExtension discoveryExtension = new ZigBeeDiscoveryExtension();
            discoveryExtension.SetUpdatePeriod(60);
            networkManager.AddExtension(discoveryExtension);

            // Initialise the network
            networkManager.Initialize();

            // Add events listeners
            networkManager.AddCommandListener(new ZigBeeTransaction(networkManager));
            networkManager.AddCommandListener(new ZigBeeCommandListener(this));
            networkManager.AddNetworkNodeListener(new ZigBeeNetworkNodeListener(this));
            // Add other extensions
            networkManager.AddSupportedClientCluster(ZclOnOffCluster.CLUSTER_ID);
            networkManager.AddSupportedClientCluster(ZclColorControlCluster.CLUSTER_ID);
            networkManager.AddExtension(new ZigBeeBasicServerExtension());
            networkManager.AddExtension(new ZigBeeIasCieExtension());

            // Start network manager
            ZigBeeStatus startupSucceded = networkManager.Startup(false);
            
            if (startupSucceded == ZigBeeStatus.SUCCESS)
            {
                // get stored node list
                for (int i = 0; i < networkManager.Nodes.Count; i++)
                {
                    var node = networkManager.Nodes[i];
                    AddNode(node);
                }
                return true;
            }

            return false;
        }

        public void Disconnect()
        {
            if (networkManager != null && networkManager.Transport != null)
            {
                zigbeePort.Close();
                transportTransmit.Shutdown();
                networkManager.Shutdown();
                zigbeePort = null;
                transportTransmit = null;
                networkManager = null;
            }
            modules.Clear();
        }

        public bool IsDevicePresent()
        {
            return true;
        }

        #endregion
        
        public void RouteEvent(ZigBeeNode node, string eventDescription, string propertyPath, object propertyValue)
        {
            OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), eventDescription, propertyPath, propertyValue);
        }
        
        protected virtual void OnInterfaceModulesChanged(string domain)
        {
            if (InterfaceModulesChanged != null)
            {
                var args = new InterfaceModulesChangedEventArgs(domain);
                InterfaceModulesChanged(this, args);
            }
        }
        protected virtual void OnInterfacePropertyChanged(string domain, string source, string description, string propertyPath, object propertyValue)
        {
            if (InterfacePropertyChanged != null)
            {
                var args = new InterfacePropertyChangedEventArgs(domain, source, description, propertyPath, propertyValue);
                new Thread(() =>
                {
                    InterfacePropertyChanged(this, args);
                }).Start();
            }
        }

        public void AddNode(ZigBeeNode node)
        {
            if (node.LogicalType == NodeDescriptor.LogicalType.ROUTER || node.LogicalType == NodeDescriptor.LogicalType.END_DEVICE)
            {
                string address = node.IeeeAddress.ToString();
                modules.RemoveAll((m) => m.Address == address);
                modules.Add(new InterfaceModule()
                {
                    Domain = "HomeAutomation.ZigBee",
                    Address = address,
                    CustomData = new ZigBeeNodeData()
                    {
                        // TODO: get type from device node
                        Type = ModuleTypes.Generic
                    }
                });
                lastAddedNode = node.IeeeAddress.ToString();
                OnInterfacePropertyChanged(this.GetDomain(), "0", "ZigBee Controller", "Controller.Status", "Added node " + node.IeeeAddress);
                // get manufacturer name and model identifier
                ReadClusterData(node);
                OnInterfaceModulesChanged(this.GetDomain());
            }
        }

        public void RemoveNode(ZigBeeNode node)
        {
            int removed = modules.RemoveAll((m) => m.Address == node.IeeeAddress.ToString());
            if (removed > 0)
            {
                lastRemovedNode = node.IeeeAddress.ToString();
                OnInterfacePropertyChanged(this.GetDomain(), "0", "ZigBee Controller", "Controller.Status", "Removed node " + node.IeeeAddress);
                OnInterfaceModulesChanged(this.GetDomain());
            }
        }

        public void UpdateNode(ZigBeeNode node)
        {
            // TODO: ....
        }

        public ZigBeeNode GetNode(ushort nodeId)
        {
            return networkManager.GetNode(nodeId);
        }
        
        private void SoftReset()
        {
            if (networkManager != null)
            {
                networkManager.Nodes.ForEach((n) => networkManager.RemoveNode(n));
                networkManager.DatabaseManager.Clear();
                Connect();
            }
        }

        private async Task SetLevel(ZigBeeEndpointAddress endpointAddress, int level, ushort transition)
        {
            var cmd = new MoveToLevelWithOnOffCommand()
            {
                Level = (byte)(level*2.55D),
                TransitionTime = transition
            };
            await networkManager.Send(endpointAddress, cmd);
        }
        private async Task SetColorHsb(ZigBeeEndpointAddress endpointAddress, double h, double s, double v, ushort transition)
        {
            var rgb = ColorHelper.HSVToRGB(new ColorHelper.HSV(h, s, v));
            CieColor xyY = ColorConverter.RgbToCie(rgb.R, rgb.G, rgb.B);
            MoveToColorCommand cmd = new MoveToColorCommand()
            {
                ColorX = xyY.X,
                ColorY = xyY.Y,
                TransitionTime = transition
            };
            await networkManager.Send(endpointAddress, cmd);
        }

        private async Task ReadClusterData(ZigBeeNode node)
        {
            var endpoint = node.GetEndpoints().FirstOrDefault();
            var cluster = endpoint?.GetInputCluster(ZclBasicCluster.CLUSTER_ID);
            if (cluster != null)
            {
                string modelIdentifier = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MODELIDENTIFIER));
                if (modelIdentifier != null)
                {
                    OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_ModelIdentifier, modelIdentifier);
                }
                string manufacturerName = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MANUFACTURERNAME));
                if (manufacturerName != null)
                {
                    OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_ManufacturerName, manufacturerName);
                }
                // try to probe / read current device values
                var module = modules.Find((m) => m.Address == node.IeeeAddress.ToString());
                // Color Light Bulb
                if (module != null && module.CustomData.Type == ModuleTypes.Generic)
                {
                    try
                    {
                        var colorCluster = endpoint?.GetInputCluster(ZclColorControlCluster.CLUSTER_ID);
                        //byte colorCapabilities = (byte)(await colorCluster.ReadAttributeValue(ZclColorControlCluster.ATTR_COLORMODE));
                        //OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", ModuleEvents.Status_Level, level / 254D);
                        if (colorCluster != null)
                        {
                            module.CustomData.Type = ModuleTypes.Color;
                            return;
                        }
                    }
                    catch (Exception e)
                    {
//                    Console.WriteLine(e);
                    }
                    // Dimmer
                    try
                    {
                        // Binary switch level / basic.get
                        var levelCluster = endpoint?.GetInputCluster(ZclLevelControlCluster.CLUSTER_ID);
                        byte level = (byte)(await levelCluster.ReadAttributeValue(ZclLevelControlCluster.ATTR_CURRENTLEVEL));
                        OnInterfacePropertyChanged
                        (
                            this.GetDomain(),
                            node.IeeeAddress.ToString(),
                            "ZigBee Node",
                            ModuleEvents.Status_Level,
                            level / 254D
                        );
                        module.CustomData.Type = ModuleTypes.Dimmer;
                        return;
                    }
                    catch (Exception e)
                    {
//                    Console.WriteLine(e);
                    }
                    // Sensor
                    try
                    {
                        var levelCluster = endpoint?.GetInputCluster(ZclIasZoneCluster.CLUSTER_ID);
                        byte level = (byte)(await levelCluster.ReadAttributeValue(ZclIasZoneCluster.ATTR_ZONESTATUS));
                        OnInterfacePropertyChanged
                        (
                            this.GetDomain(),
                            node.IeeeAddress.ToString(),
                            "ZigBee Node",
                            ModuleEvents.Status_Level,
                            level
                        );
                        module.CustomData.Type = ModuleTypes.Sensor;
                    }
                    catch (Exception e)
                    {
//                    Console.WriteLine(e);
                    }
                }
            }
        }

        private async Task ReadClusterLevelData(ZigBeeNode node, ZigBeeEndpoint endpoint)
        {
            var cluster = endpoint.GetInputCluster(ZclLevelControlCluster.CLUSTER_ID);
            if (cluster != null)
            {
                byte level = (byte)(await cluster.ReadAttributeValue(ZclLevelControlCluster.ATTR_CURRENTLEVEL));
                OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", ModuleEvents.Status_Level, level / 254D);
            }
        }

        private async Task DiscoverAttributes(ZigBeeEndpoint endpoint)
        {
            foreach (int clusterId in endpoint.GetInputClusterIds())
            {
                ZclCluster cluster = endpoint.GetInputCluster(clusterId);
                if (!await cluster.DiscoverAttributes(true))
                    Console.WriteLine("Error while discovering attributes for cluster {0}", cluster.GetClusterName());
            }
        }
    }

    public class ZigBeeNodeData
    {
        private double level = 0;
        public double Level {
            get => level;
            set
            {
                level = value;
                if (level != 0)
                {
                    LastLevel = level;
                }
            }
        }
        public double LastLevel;
        public ushort Transition = 4; // 400ms
        public ModuleTypes Type = ModuleTypes.Generic;
    }
    
    public class ZigBeeCommandListener : IZigBeeCommandListener
    {
        private ZigBee _zigBee;

        public ZigBeeCommandListener(ZigBee zigBee)
        {
            _zigBee = zigBee;
        }
        public void CommandReceived(ZigBeeCommand command)
        {
            // TODO: parse / handle received commands
            if (_zigBee != null)
            {
                
                var nodeId = command.SourceAddress.Address;
                var node = _zigBee.GetNode(nodeId);
                if (command is DeviceAnnounce)
                {
//                    Console.WriteLine("DEVICE ANNOUNCE");
                }
                else if (command is ReportAttributesCommand)
                {
                    var cmd = (ReportAttributesCommand)command;
                    switch (command.ClusterId)
                    {
                        case ZclTemperatureMeasurementCluster.CLUSTER_ID:
                            CheckType(node, ModuleTypes.Sensor);
                            _zigBee.RouteEvent(
                                node,
                                "ZigBee Node",
                                ModuleEvents.Sensor_Temperature,
                                (Int16)cmd.Reports[0].AttributeValue / 100D
                            );
                            break;
                        case ZclRelativeHumidityMeasurementCluster.CLUSTER_ID:
                            CheckType(node, ModuleTypes.Sensor);
                            _zigBee.RouteEvent(
                                node,
                                "ZigBee Node",
                                ModuleEvents.Sensor_Humidity,
                                (UInt16)cmd.Reports[0].AttributeValue / 100D
                            );
                            break;
                        case ZclIlluminanceLevelSensingCluster.CLUSTER_ID:
                            CheckType(node, ModuleTypes.Sensor);
                            _zigBee.RouteEvent(
                                node,
                                "ZigBee Node",
                                ModuleEvents.Sensor_Luminance,
                                (UInt16)cmd.Reports[0].AttributeValue
                            );
                            break;
                    }
                }
                else if (command is ZoneStatusChangeNotificationCommand)
                {
                    var cmd = (ZoneStatusChangeNotificationCommand)command;
                    switch (command.ClusterId)
                    {
                        case ZclIasZoneCluster.CLUSTER_ID:
                            // TODO: could map to generic "Sensor" or "DoorWindow"
                            CheckType(node, ModuleTypes.Sensor);
                            _zigBee.RouteEvent(
                                node,
                                "ZigBee Node",
                                ModuleEvents.Status_Level,
                                (Int16)cmd.ZoneStatus
                            );
                            break;
                    }
                }
            }
            Console.WriteLine(command);
        }

        private void CheckType(ZigBeeNode node, ModuleTypes moduleType)
        {
            var module = _zigBee.GetModules().Find((m) => m.Address == node.IeeeAddress.ToString());
            var moduleData = (module.CustomData as ZigBeeNodeData);
            if (module != null && moduleData != null && moduleData.Type == ModuleTypes.Generic)
            {
                moduleData.Type = moduleType;
            }
        }
    }

    public class ZigBeeNetworkNodeListener : IZigBeeNetworkNodeListener
    {
        private readonly ZigBee zigBee;
        public ZigBeeNetworkNodeListener(ZigBee zigBeeInterface)
        {
            zigBee = zigBeeInterface;
        }
        public void NodeAdded(ZigBeeNode node)
        {
            if (node.NetworkAddress != 0)
            {
                zigBee.AddNode(node);
            }
        }

        public void NodeRemoved(ZigBeeNode node)
        {
            zigBee.RemoveNode(node);
        }

        public void NodeUpdated(ZigBeeNode node)
        {
            zigBee.UpdateNode(node);
        }
    }
}
