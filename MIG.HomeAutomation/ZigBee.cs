/*
  This file is part of MIG (https://github.com/genielabs/mig-service-dotnet)
 
  Copyright (2012-2023) G-Labs (https://github.com/genielabs)

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

/*
 *     Author: Generoso Martello <gene@homegenie.it>
 *     Project Homepage: https://github.com/genielabs/mig-service-dotnet
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
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
using ZigBeeNet.ZCL.Clusters;
using ZigBeeNet.ZCL.Clusters.ColorControl;
using ZigBeeNet.ZCL.Clusters.General;
using ZigBeeNet.ZCL.Clusters.IasZone;
using ZigBeeNet.ZCL.Clusters.Identify;
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
            DoorLock_Get,
            Binding_Get,
            Binding_Ensure
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
        const string EventPath_GenericDeviceType
            = "ZigBeeNode.GenericDeviceType";
        const string EventPath_Endpoints
            = "ZigBeeNode.Endpoints";

        #endregion
        
        #region Private fields

        private const int NumberOfAddAttempts = 120;
        private const int NumberOfRemoveAttempts = 60;
        private const int DelayBetweenAttempts = 500;

        private ZigBeeNetworkManager networkManager;
        private ZigBeeSerialPort zigbeePort;
        private IZigBeeTransportTransmit transportTransmit;

        private readonly object inclusionLock = new object();
        private Inclusion pendingInclusion;
        private readonly SemaphoreSlim bindingLock = new SemaphoreSlim(1);
        private string lastRemovedNode;
        private const string ZigBeeModulesDb = "zigbee_modules.xml";

        #endregion
        
        #region MIG Interface members

        public event InterfaceModulesChangedEventHandler InterfaceModulesChanged;
        public event InterfacePropertyChangedEventHandler InterfacePropertyChanged;

        public bool IsEnabled { get; set; }

        public ZigBee() { }

        internal ZigBee(ZigBeeNetworkManager manager)
        {
            networkManager = manager;
        }

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
            if (command == Commands.Binding_Get || command == Commands.Binding_Ensure)
            {
                if (request.Address.Length != 16 || !ulong.TryParse(request.Address, NumberStyles.AllowHexSpecifier,
                        CultureInfo.InvariantCulture, out _) ||
                    !byte.TryParse(request.GetOption(0), NumberStyles.None, CultureInfo.InvariantCulture, out var ep) ||
                    ep < 1 || ep > 240 || !ZigBeeBinding.TryParseCluster(request.GetOption(1), out var clusterId))
                    return new ResponseStatus(Status.Error, "Expected device IEEE address, endpoint (1-240) and cluster (0x0006 or 0x0008).");
                var manager = networkManager;
                var target = manager?.GetNode(new IeeeAddress(request.Address));
                if (target == null || target.NetworkAddress == 0)
                    return new ResponseStatus(Status.Error, "Unknown ZigBee device.");
                return InspectBinding(manager, target, ep, clusterId, command == Commands.Binding_Ensure).GetAwaiter().GetResult();
            }
            var node = networkManager.GetNode(new IeeeAddress(request.Address));
            
            // controller commands

            if (request.Address == "0") // "0" = controller
            {

                switch (command)
                {
                    case Commands.Controller_NodeAdd:
                        return IncludeNode(networkManager);
                    case Commands.Controller_NodeRemove:
                        var nodeId = request.GetOption(0);
                        var nodeToRemove = networkManager.GetNode(new IeeeAddress(nodeId));
                        if (nodeToRemove != null && nodeToRemove.NetworkAddress > 0)
                        {
                            lastRemovedNode = "";
                            new Thread(()=>
                            {
                                networkManager
                                    .Leave(nodeToRemove.NetworkAddress, nodeToRemove.IeeeAddress, true)
                                    .Wait();
                                if (String.IsNullOrEmpty(lastRemovedNode)) RemoveNode(nodeToRemove);
                            }).Start();
                            for (int i = 0; i < NumberOfRemoveAttempts; i++)
                            {
                                if (!String.IsNullOrEmpty(lastRemovedNode))
                                {
                                    break;
                                }
                                Thread.Sleep(DelayBetweenAttempts);
                            }
                            returnValue = new ResponseStatus(Status.Ok, $"Removed node {lastRemovedNode}.");
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
            if (command == Commands.Basic_Get)
            {
                try
                {
                    return ReadClusterLevelData(node).GetAwaiter().GetResult()
                        ? new ResponseStatus(Status.Ok)
                        : new ResponseStatus(Status.Error, "No on/off or level value available.");
                }
                catch (Exception e)
                {
                    return new ResponseStatus(Status.Error, e.Message);
                }
            }
            // Target the endpoint advertising the command, including on multi-endpoint lights.
            var endpoint = node.GetEndpoints().FirstOrDefault();
            int requiredCluster = -1;
            switch (command)
            {
                case Commands.Control_On:
                case Commands.Control_Off:
                case Commands.Control_Toggle: requiredCluster = ZclOnOffCluster.CLUSTER_ID; break;
                case Commands.Control_Level: requiredCluster = ZclLevelControlCluster.CLUSTER_ID; break;
                case Commands.Control_ColorHsb: requiredCluster = ZclColorControlCluster.CLUSTER_ID; break;
            }
            if (requiredCluster >= 0)
                endpoint = node.GetEndpoints().FirstOrDefault(e => e.GetInputClusterIds().Contains(requiredCluster));
            if (endpoint == null)
            {
                return new ResponseStatus(Status.Error, "Could not determine node endpoint address.");
            }
            ZigBeeEndpointAddress endpointAddress = endpoint.GetEndpointAddress();

            switch (command)
            {
                case Commands.Control_On:
                    networkManager
                        .Send(endpointAddress, new OnCommand());
                    nodeData.SetOnOff(true);
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
                    nodeData.SetOnOff(nodeData.Level <= 0);
                    networkManager
                        .Send(endpointAddress, new ToggleCommand());
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
                    QueryNodeData(node);
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
            // load cached modules
            lock (modules) DeserializeModules(ZigBeeModulesDb, modules);
            // initialize controller
            string portName = this.GetOption("Port")?.Value;
            if (String.IsNullOrEmpty(portName))
            {
                return false;
            }

            string fc = this.GetOption("FlowControl")?.Value;
            var flowControl = fc == "1" ? FlowControl.FLOWCONTROL_OUT_XONOFF : fc == "2" ? FlowControl.FLOWCONTROL_OUT_RTSCTS : FlowControl.FLOWCONTROL_OUT_NONE;
            string br = this.GetOption("BaudRate")?.Value;
            if (!int.TryParse(br, out var baudRate))
            {
                baudRate = 115200;
            }

            zigbeePort = new ZigBeeSerialPort(portName, baudRate, flowControl);

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
            networkManager = transportTransmit is ZigbeeDongleConBee conBee
                ? new ConBeeNetworkManager(conBee)
                : new ZigBeeNetworkManager(transportTransmit);

            var dataStore = new JsonNetworkDataStore(GetDbFullPath());
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
            networkManager.AddSupportedClientCluster(ZclLevelControlCluster.CLUSTER_ID);
            networkManager.AddSupportedClientCluster(ZclColorControlCluster.CLUSTER_ID);
            networkManager.AddExtension(new ZigBeeBasicServerExtension());
            networkManager.AddExtension(new ZigBeeIasCieExtension());

            // Start network manager
            ZigBeeStatus startupSucceded = networkManager.Startup(false);
            
            if (startupSucceded == ZigBeeStatus.SUCCESS)
            {
                // disable node joining
                ZigBeeNode coord = networkManager.GetNode(0);
                coord.PermitJoin(false);
                OnInterfaceModulesChanged(this.GetDomain());
                return true;
            }

            return false;
        }

        public void Disconnect()
        {
            lock (inclusionLock) pendingInclusion?.Completed.TrySetResult(null);
            if (networkManager != null && networkManager.Transport != null)
            {
                zigbeePort.Close();
                transportTransmit.Shutdown();
                networkManager.Shutdown();
                zigbeePort = null;
                transportTransmit = null;
                networkManager = null;
            }
            lock (modules) modules.Clear();
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

        public void ControllerEvent(string eventPath, string eventPayload)
        {
            OnInterfacePropertyChanged(this.GetDomain(), "0", "ZigBee Controller", eventPath, eventPayload);
        }
        
        public ZigBeeNode GetNode(ushort nodeId)
        {
            return networkManager?.GetNode(nodeId);
        }

        internal sealed class Inclusion
        {
            private readonly HashSet<string> known;
            internal readonly TaskCompletionSource<ZigBeeNode> Completed = new TaskCompletionSource<ZigBeeNode>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            internal Inclusion(IEnumerable<string> knownAddresses)
            {
                known = new HashSet<string>(knownAddresses, StringComparer.OrdinalIgnoreCase);
            }

            internal void Observe(ZigBeeNode node)
            {
                if (node.NetworkAddress != 0 && node.IsDiscovered() && !known.Contains(node.IeeeAddress.ToString()))
                    Completed.TrySetResult(node);
            }
        }

        private ResponseStatus IncludeNode(ZigBeeNetworkManager manager)
        {
            Inclusion inclusion;
            lock (inclusionLock)
            {
                if (pendingInclusion != null) return new ResponseStatus(Status.Error, "Inclusion already in progress.");
                lock (modules)
                    inclusion = new Inclusion(manager.Nodes.Select(n => n.IeeeAddress.ToString()).Concat(modules.Select(m => m.Address)));
                pendingInclusion = inclusion;
            }

            ZigBeeNode coord = null;
            ZigBeeNode added = null;
            try
            {
                coord = manager.GetNode(0);
                if (coord == null) return new ResponseStatus(Status.Error, "Coordinator unavailable.");
                coord.PermitJoin(true);
                if (inclusion.Completed.Task.Wait(NumberOfAddAttempts * DelayBetweenAttempts))
                    added = inclusion.Completed.Task.Result;
            }
            finally
            {
                try
                {
                    if (manager == networkManager) coord?.PermitJoin(false);
                }
                finally
                {
                    lock (inclusionLock) pendingInclusion = null;
                }
            }
            if (added != null) _ = Task.Run(() => ConfigureIncludedLoad(manager, added));
            return new ResponseStatus(Status.Ok, added?.IeeeAddress.ToString() ?? "");
        }

        private async Task ConfigureIncludedLoad(ZigBeeNetworkManager manager, ZigBeeNode node)
        {
            try
            {
                foreach (var ep in node.GetEndpoints().Where(e => ZigBeeBinding.IsLoadEndpoint(e.ProfileId, e.DeviceId)))
                {
                    foreach (var cluster in ep.GetInputClusterIds().Where(id => id == ZclOnOffCluster.CLUSTER_ID || id == ZclLevelControlCluster.CLUSTER_ID))
                    {
                        if (networkManager != manager) return;
                        var result = await InspectBinding(manager, node, ep.EndpointId, (ushort)cluster, true).ConfigureAwait(false);
                        if (manager == networkManager)
                            RouteEvent(node, "ZigBee binding", $"ZigBeeNode.Binding.{ep.EndpointId}.{cluster:X4}",
                                result is ZigBeeBinding binding ? binding.Outcome : result.Message);
                    }
                }
            }
            catch (Exception e) { MigService.Log.Debug($"ZigBee inclusion binding: {e.Message}"); }
        }

        private async Task<ResponseStatus> InspectBinding(ZigBeeNetworkManager manager, ZigBeeNode node, byte ep, ushort cluster, bool ensure)
        {
            await bindingLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var coord = manager.GetNode(0);
                if (manager != networkManager || coord == null || manager.GetNode(node.IeeeAddress) != node)
                    return new ResponseStatus(Status.Error, "Device or coordinator unavailable.");
                var binding = new ZigBeeBinding(node, ep, cluster, coord.IeeeAddress);
                await binding.Inspect(ensure).ConfigureAwait(false);
                return binding;
            }
            catch (Exception e) { return new ResponseStatus(Status.Error, e.Message); }
            finally { bindingLock.Release(); }
        }

        private void ObserveInclusion(ZigBeeNode node)
        {
            lock (inclusionLock)
            {
                if (pendingInclusion != null && networkManager?.GetNode(node.IeeeAddress) == node)
                    pendingInclusion.Observe(node);
            }
        }

        public void AddNode(ZigBeeNode node)
        {
            if (node.LogicalType == NodeDescriptor.LogicalType.ROUTER || node.LogicalType == NodeDescriptor.LogicalType.END_DEVICE)
            {
                string address = node.IeeeAddress.ToString();
                InterfaceModule module;
                lock (modules)
                {
                    module = modules.Find(m => m.Address == address);
                    if (module == null)
                    {
                        module = new InterfaceModule { Domain = "HomeAutomation.ZigBee", Address = address, CustomData = new ZigBeeNodeData() };
                        modules.Add(module);
                    }
                }
                if (module.CustomData.Type == ModuleTypes.Generic || String.IsNullOrEmpty(module.CustomData.ManufacturerName) || String.IsNullOrEmpty(module.CustomData.ModelIdentifier))
                {
                    ControllerEvent("Controller.Status", "Added node " + node.IeeeAddress);
                    // get manufacturer name and model identifier
                    var t = new Thread(() =>
                    {
                        try
                        {
                            QueryNodeData(node)
                                .Wait();
                        }
                        catch (Exception e)
                        {
                            //Console.WriteLine(e);
                        }

                        OnInterfaceModulesChanged(this.GetDomain());
                    });
                    t.Start();
                    t.Join();
                }
                ObserveInclusion(node);
            }
        }

        public void RemoveNode(ZigBeeNode node)
        {
            int removed;
            lock (modules) removed = modules.RemoveAll(m => m.Address == node.IeeeAddress.ToString());
            if (removed > 0)
            {
                ControllerEvent("Controller.Status", "Removed node " + node.IeeeAddress);
                OnInterfaceModulesChanged(this.GetDomain());
                lastRemovedNode = node.IeeeAddress.ToString();
            }
        }

        public void UpdateNode(ZigBeeNode node)
        {
            ObserveInclusion(node);
        }

        public void UpdateModules()
        {
            SerializeModules(ZigBeeModulesDb, modules);
        }

        private void SerializeModules(string fileName, List<InterfaceModule> list) {
            lock (this)
            {
                try
                {
                    XmlAttributeOverrides overrides = new XmlAttributeOverrides();
                    XmlAttributes attribs = new XmlAttributes();
                    attribs.Xmlns = false;
                    attribs.XmlElements.Add(new XmlElementAttribute("CustomData", typeof(ZigBeeNodeData)));
                    overrides.Add(typeof(InterfaceModule), "CustomData", attribs);
                    var serializer = new XmlSerializer(typeof(List<InterfaceModule>), overrides);
                    XmlWriterSettings settings = new XmlWriterSettings
                    {
                        Indent = true,
                        OmitXmlDeclaration = true
                    };
                    var emptyNamespaces = new XmlSerializerNamespaces(new[] { XmlQualifiedName.Empty });
                    using (XmlWriter xmlWriter = XmlWriter.Create(GetDbFullPath(fileName), settings))
                    {
                        serializer.Serialize(xmlWriter, list, emptyNamespaces); 
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
        private void DeserializeModules(string fileName, List<InterfaceModule> list) {
            try
            {
                fileName = GetDbFullPath(fileName);
                if (File.Exists(fileName))
                {
                    XmlAttributeOverrides overrides = new XmlAttributeOverrides();
                    XmlAttributes attribs = new XmlAttributes();
                    attribs.XmlElements.Add(new XmlElementAttribute("CustomData", typeof(ZigBeeNodeData)));
                    overrides.Add(typeof(InterfaceModule), "CustomData", attribs);
                    var serializer = new XmlSerializer(typeof(List<InterfaceModule>), overrides);
                    using (var stream = File.OpenRead(fileName))
                    {
                        var other = (List<InterfaceModule>)(serializer.Deserialize(stream));
                        list.Clear();
                        list.AddRange(other);
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private CancellationTokenSource _updateModulesTokenSource;
        protected virtual void OnInterfaceModulesChanged(string domain)
        {
            if (_updateModulesTokenSource != null)
            {
                _updateModulesTokenSource.Cancel();
            }
            _updateModulesTokenSource = new CancellationTokenSource();
            new Thread((token) =>
            {
                Thread.Sleep(500);
                if (((CancellationToken)token).IsCancellationRequested)
                {
                    return;
                }
                if (InterfaceModulesChanged != null)
                {
                    var args = new InterfaceModulesChangedEventArgs(domain);
                    InterfaceModulesChanged(this, args);
                }
                UpdateModules();
            }).Start(_updateModulesTokenSource.Token);
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

        private static string GetDbFullPath(string file = "")
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "mig", "zigbee");
            Directory.CreateDirectory(path);
            return !String.IsNullOrEmpty(file) ? Path.Combine(path, file) : path;
        }

        private void SoftReset()
        {
            if (networkManager != null)
            {
                networkManager.Nodes.ForEach((n) => networkManager.RemoveNode(n));
                networkManager.DatabaseManager.Clear();
            }
            // delete zigbee modules database
            var fileName = GetDbFullPath(ZigBeeModulesDb);
            if (File.Exists(fileName)) {
                File.Delete(fileName);
            }
            Connect();
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

        private async Task QueryNodeData(ZigBeeNode node)
        {
            OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_Endpoints, node.GetEndpoints().Count);
            bool occupancySensing = false;
            bool colorControl = false;
            bool levelControl = false;
            bool onOff = false;
            bool iasZone = false;
            bool electricalMeasurement = false;
            bool illuminanceMeasurement = false;
            bool temperatureMeasurement = false;
            bool windowCovering = false;
            bool metering = false;
            int ec = 0;
            foreach (var endpoint in node.GetEndpoints())
            {
                OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", $"{EventPath_Endpoints}.{ec}.ID", endpoint.EndpointId);
                foreach (var clusterId in endpoint.GetInputClusterIds())
                {
                    switch (clusterId)
                    {
                        case ZclOnOffCluster.CLUSTER_ID:
                            onOff = true;
                            break;
                        case ZclIasZoneCluster.CLUSTER_ID:
                            iasZone = true;
                            break;
                        case ZclColorControlCluster.CLUSTER_ID:
                            colorControl = true;
                            break;
                        case ZclLevelControlCluster.CLUSTER_ID:
                            levelControl = true;
                            break;
                        case ZclElectricalMeasurementCluster.CLUSTER_ID:
                            electricalMeasurement = true;
                            break;
                        case ZclIlluminanceLevelSensingCluster.CLUSTER_ID:
                        case ZclIlluminanceMeasurementCluster.CLUSTER_ID:
                            illuminanceMeasurement = true;
                            break;
                        case ZclOccupancySensingCluster.CLUSTER_ID:
                            occupancySensing = true;
                            break;
                        case ZclWindowCoveringCluster.CLUSTER_ID:
                            windowCovering = true;
                            break;
                        case ZclTemperatureMeasurementCluster.CLUSTER_ID:
                            temperatureMeasurement = true;
                            break;
                        case ZclMeteringCluster.CLUSTER_ID:
                            metering = true;
                            break;
                    }
                    var clusterName = endpoint.GetInputCluster(clusterId)
                        .GetClusterName();
                    OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", $"{EventPath_Endpoints}.{ec}.{clusterId}", clusterName);
                }
                ec++;
            }
            bool updateModules = false;
            var module = modules.Find((m) => m.Address == node.IeeeAddress.ToString());
            var defaultEndpoint = node.GetEndpoints().FirstOrDefault();
            if (defaultEndpoint != null)
            {
                // Get manufacturer / model
                var cluster = defaultEndpoint.GetInputCluster(ZclBasicCluster.CLUSTER_ID);
                if (cluster != null)
                {
                    string manufacturerName = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MANUFACTURERNAME));
                    if (manufacturerName != null)
                    {
                        (module?.CustomData as ZigBeeNodeData).ManufacturerName = manufacturerName; 
                        OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_ManufacturerName, manufacturerName);
                    }
                    string modelIdentifier = (string)(await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_MODELIDENTIFIER));
                    if (modelIdentifier != null)
                    {
                        (module?.CustomData as ZigBeeNodeData).ModelIdentifier = modelIdentifier; 
                        OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_ModelIdentifier, modelIdentifier);
                    }
                    var genericDeviceType = (await cluster.ReadAttributeValue(ZclBasicCluster.ATTR_GENERICDEVICETYPE));
                    if (genericDeviceType != null)
                    {
                        OnInterfacePropertyChanged(this.GetDomain(), node.IeeeAddress.ToString(), "ZigBee Node", EventPath_GenericDeviceType, genericDeviceType);
                    }
                    updateModules = manufacturerName != null || modelIdentifier != null || genericDeviceType != null;
                }
            }
            // Probe device type
            if (module != null && module.CustomData.Type == ModuleTypes.Generic)
            {
                if (colorControl)
                {
                    module.CustomData.Type = ModuleTypes.Color;
                }
                else if (levelControl && !occupancySensing)
                {
                    module.CustomData.Type = ModuleTypes.Color;
                }
                else if (iasZone || occupancySensing || illuminanceMeasurement || temperatureMeasurement || windowCovering || electricalMeasurement || metering)
                {
                    module.CustomData.Type = ModuleTypes.Sensor;
                }
                else if (onOff)
                {
                    module.CustomData.Type = ModuleTypes.Switch;
                }
                updateModules = updateModules || module.CustomData.Type != ModuleTypes.Generic;
            }
            if (updateModules)
            {
                UpdateModules();
            }
        }

        private async Task<bool> ReadClusterLevelData(ZigBeeNode node)
        {
            var endpoint = node.GetEndpoints().FirstOrDefault(e => e.GetInputClusterIds().Contains(ZclOnOffCluster.CLUSTER_ID))
                ?? node.GetEndpoints().FirstOrDefault(e => e.GetInputClusterIds().Contains(ZclLevelControlCluster.CLUSTER_ID));
            if (endpoint == null) return false;
            var address = node.IeeeAddress.ToString();
            var onOff = endpoint.GetInputCluster(ZclOnOffCluster.CLUSTER_ID);
            var level = endpoint.GetInputCluster(ZclLevelControlCluster.CLUSTER_ID);
            var on = onOff == null ? null : await onOff.ReadAttributeValue(ZclOnOffCluster.ATTR_ONOFF).ConfigureAwait(false);
            if (!(on is false) && level != null)
            {
                var value = await level.ReadAttributeValue(ZclLevelControlCluster.ATTR_CURRENTLEVEL).ConfigureAwait(false);
                if (value is byte current && current < byte.MaxValue)
                {
                    ReportAttribute(address, ZclLevelControlCluster.CLUSTER_ID, ZclLevelControlCluster.ATTR_CURRENTLEVEL, current);
                    return true;
                }
            }
            if (!(on is bool)) return false;
            ReportAttribute(address, ZclOnOffCluster.CLUSTER_ID, ZclOnOffCluster.ATTR_ONOFF, on);
            return true;
        }

        internal void ReceiveReport(string address, ZigBeeCommand command)
        {
            string action = null;
            switch (command)
            {
                case OnCommand _:
                    action = "On";
                    break;
                case OffCommand _:
                case OffWithEffectCommand _:
                    action = "Off";
                    break;
                case StepCommand step when step.StepMode == 0:
                    action = "BrightnessUp";
                    break;
                case StepCommand step when step.StepMode == 1:
                    action = "BrightnessDown";
                    break;
                case StopCommand _:
                    action = "BrightnessStop";
                    break;
            }
            if (action != null)
                OnInterfacePropertyChanged(this.GetDomain(), address, "ZigBee Remote", ModuleEvents.Receiver_Action, action);
            else if (command is ZoneStatusChangeNotificationCommand zone && command.ClusterId == ZclIasZoneCluster.CLUSTER_ID)
                ReportZoneStatus(address, zone.ZoneStatus);
            else if (command is ReportAttributesCommand attributes && attributes.Reports != null)
                foreach (var report in attributes.Reports)
                    ReportAttribute(address, command.ClusterId, report.AttributeIdentifier, report.AttributeValue);
        }

        private void ReportAttribute(string address, ushort cluster, ushort attribute, object value)
        {
            void Publish(string path, object reading) => OnInterfacePropertyChanged(this.GetDomain(), address, "ZigBee Node", path, reading);
            switch (cluster)
            {
                case ZclTemperatureMeasurementCluster.CLUSTER_ID when attribute == ZclTemperatureMeasurementCluster.ATTR_MEASUREDVALUE && value is short temperature && temperature >= -27315:
                    Publish(ModuleEvents.Sensor_Temperature, temperature / 100D);
                    break;
                case ZclRelativeHumidityMeasurementCluster.CLUSTER_ID when attribute == ZclRelativeHumidityMeasurementCluster.ATTR_MEASUREDVALUE && value is ushort humidity && humidity <= 10000:
                    Publish(ModuleEvents.Sensor_Humidity, humidity / 100D);
                    break;
                case ZclIlluminanceMeasurementCluster.CLUSTER_ID when attribute == ZclIlluminanceMeasurementCluster.ATTR_MEASUREDVALUE && value is ushort measured && measured != ushort.MaxValue:
                    Publish(ModuleEvents.Sensor_Luminance, measured == 0 ? 0D : Math.Pow(10, (measured - 1) / 10000D));
                    break;
                case ZclIlluminanceLevelSensingCluster.CLUSTER_ID when attribute == ZclIlluminanceLevelSensingCluster.ATTR_LEVELSTATUS && value is byte status && status <= 2:
                    Publish("ZigBeeNode.IlluminanceLevelStatus", status);
                    break;
                case ZclOccupancySensingCluster.CLUSTER_ID when attribute == ZclOccupancySensingCluster.ATTR_OCCUPANCY && value is byte occupancy:
                    Publish(ModuleEvents.Sensor_MotionDetect, occupancy & 1);
                    break;
                case ZclIasZoneCluster.CLUSTER_ID when attribute == ZclIasZoneCluster.ATTR_ZONESTATUS && value is ushort zone:
                    ReportZoneStatus(address, zone);
                    break;
                case ZclOnOffCluster.CLUSTER_ID when attribute == ZclOnOffCluster.ATTR_ONOFF && value is bool on:
                    var switchData = modules.Find(m => m.Address == address)?.CustomData as ZigBeeNodeData;
                    switchData?.SetOnOff(on);
                    Publish(ModuleEvents.Status_Level, switchData?.Level ?? (on ? 1D : 0D));
                    break;
                case ZclLevelControlCluster.CLUSTER_ID when attribute == ZclLevelControlCluster.ATTR_CURRENTLEVEL && value is byte level && level != byte.MaxValue:
                    var dimmerData = modules.Find(m => m.Address == address)?.CustomData as ZigBeeNodeData;
                    if (dimmerData != null) dimmerData.Level = level / 254D;
                    Publish(ModuleEvents.Status_Level, level / 254D);
                    break;
            }
        }

        private void ReportZoneStatus(string address, ushort value)
        {
            void Publish(string path, int reading) => OnInterfacePropertyChanged(this.GetDomain(), address, "ZigBee Node", path, reading);
            var status = (IasZoneStatusBitmap)value;
            Publish("ZigBeeNode.ZoneStatus", value);
            Publish(ModuleEvents.Status_Level, (status & (IasZoneStatusBitmap.ALARM_1 | IasZoneStatusBitmap.ALARM_2)) != 0 ? 1 : 0);
            Publish(ModuleEvents.Sensor_Tamper, (status & IasZoneStatusBitmap.TAMPER) != 0 ? 1 : 0);
            Publish("ZigBeeNode.BatteryLow", (status & IasZoneStatusBitmap.BATTERY) != 0 ? 1 : 0);
        }
    }

    internal sealed class ConBeeNetworkManager : ZigBeeNetworkManager, IZigBeeTransportReceive
    {
        internal ConBeeNetworkManager(ZigbeeDongleConBee transport) : base(transport) { }

        void IZigBeeTransportReceive.ReceiveCommand(ZigBeeApsFrame frame)
        {
            // ConBee 1.5.0 places the group ID in DestinationAddress and can deliver
            // group commands with endpoint 0. ZigBeeNet accepts endpoint 1 or 0xFF for ZCL.
            if (frame.Profile == 0x0104 && frame.DestinationEndpoint == 0 &&
                frame.DestinationAddress != 0 && frame.DestinationAddress != LocalNwkAddress && frame.DestinationAddress < 0xFFF8 &&
                (frame.Cluster == ZclOnOffCluster.CLUSTER_ID || frame.Cluster == ZclLevelControlCluster.CLUSTER_ID) &&
                frame.Payload != null && frame.Payload.Length >= 3 && (frame.Payload[0] & 0x0F) == 0x01)
            {
                frame.DestinationEndpoint = 0xFF;
            }
            base.ReceiveCommand(frame);
        }
    }

    [Serializable]
    public class ZigBeeNodeData
    {
        private double _level;
        public double Level {
            get => _level;
            set
            {
                _level = value;
                if (_level != 0)
                {
                    LastLevel = _level;
                }
            }
        }
        public double LastLevel;
        public void SetOnOff(bool on)
        {
            Level = on ? (LastLevel > 0 ? LastLevel : 1) : 0;
        }
        public ushort Transition = 4; // 400ms
        public ModuleTypes Type = ModuleTypes.Generic;
        public string ManufacturerName = "";
        public string ModelIdentifier = "";
    }
    
    public class ZigBeeCommandListener : IZigBeeCommandListener
    {
        private readonly ZigBee _zigBee;

        public ZigBeeCommandListener(ZigBee zigBeeInterface)
        {
            _zigBee = zigBeeInterface;
        }
        public void CommandReceived(ZigBeeCommand command)
        {
            MigService.Log.Debug(command);
            if (_zigBee == null || command.SourceAddress == null) return;
            var node = _zigBee.GetNode(command.SourceAddress.Address);
            if (node == null) return;
            if (command is DeviceAnnounce)
                _zigBee.ControllerEvent("Controller.Status", "Announce node " + node.IeeeAddress);
            else
                _zigBee.ReceiveReport(node.IeeeAddress.ToString(), command);
        }
    }

    public class ZigBeeNetworkNodeListener : IZigBeeNetworkNodeListener
    {
        private readonly ZigBee _zigBee;
        public ZigBeeNetworkNodeListener(ZigBee zigBeeInterface)
        {
            _zigBee = zigBeeInterface;
        }
        public void NodeAdded(ZigBeeNode node)
        {
            if (node.NetworkAddress != 0)
            {
                _zigBee.AddNode(node);
            }
        }

        public void NodeRemoved(ZigBeeNode node)
        {
            _zigBee.RemoveNode(node);
        }

        public void NodeUpdated(ZigBeeNode node)
        {
            _zigBee.UpdateNode(node);
        }
    }
}
