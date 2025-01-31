[<AutoOpen>]
module Farmer.Builders.VirtualMachine

open Farmer
open Farmer.Arm
open Farmer.FeatureFlag
open Farmer.PublicIpAddress
open Farmer.PrivateIpAddress
open Farmer.Vm
open Farmer.Helpers
open Farmer.Arm.Compute
open Farmer.Arm.Network
open Farmer.Arm.Storage
open System
open Farmer.Identity

let makeName (vmName: ResourceName) elementType =
    ResourceName $"{vmName.Value}-%s{elementType}"

type VmConfig =
    {
        Name: ResourceName
        AvailabilityZone: string option
        DiagnosticsEnabled: bool option
        DiagnosticsStorageAccount: ResourceRef<VmConfig> option

        Priority: Priority option

        Username: string option
        PasswordParameter: string option
        Size: VMSize
        OsDisk: OsDiskCreateOption
        DataDisks: DataDiskCreateOption list option

        CustomScript: string option
        CustomScriptFiles: Uri list

        DomainNamePrefix: string option

        CustomData: string option
        DisablePasswordAuthentication: bool option
        SshPathAndPublicKeys: (string * string) list option
        AadSshLogin: FeatureFlag

        VNet: ResourceRef<VmConfig>
        AddressPrefix: string
        SubnetPrefix: string
        Subnet: AutoGeneratedResource<VmConfig>
        PublicIp: ResourceRef<VmConfig> option
        IpAllocation: PublicIpAddress.AllocationMethod option
        AcceleratedNetworking: FeatureFlag option
        IpForwarding: FeatureFlag option
        IpConfigs: IpConfiguration list
        PrivateIpAllocation: PrivateIpAddress.AllocationMethod option
        LoadBalancerBackendAddressPools: LinkedResource list
        Identity: Identity.ManagedIdentity
        NetworkSecurityGroup: LinkedResource option

        Tags: Map<string, string>
    }

    member internal this.DeriveResourceName (resourceType: ResourceType) elementName =
        resourceType.resourceId (makeName this.Name elementName)

    member this.NicName = this.DeriveResourceName networkInterfaces "nic"
    member this.PublicIpId = this.PublicIp |> Option.map (fun ref -> ref.resourceId this) //(this.DeriveResourceName publicIPAddresses "ip")

    member this.PublicIpAddress =
        this.PublicIpId
        |> Option.map (fun ipid -> ArmExpression.create ($"reference({ipid.ArmExpression.Value}).ipAddress"))

    member this.Hostname =
        this.PublicIpId
        |> Option.map (fun ip -> ip.ArmExpression.Map(sprintf "%s.dnsSettings.fqdn"))

    member this.SystemIdentity = SystemIdentity this.ResourceId
    member this.ResourceId = virtualMachines.resourceId this.Name

    member this.PasswordParameterArm =
        this.PasswordParameter |> Option.defaultValue $"password-for-{this.Name.Value}"

    member private this.buildIpConfigs() =
        let subnetId = this.Subnet.resourceId this

        { // Always has at least one IP config.
            SubnetName = subnetId.Name
            LoadBalancerBackendAddressPools = this.LoadBalancerBackendAddressPools
            PublicIpAddress = this.PublicIp |> Option.map (fun x -> x.toLinkedResource this)
            PrivateIpAllocation = this.PrivateIpAllocation
            Primary = if this.IpConfigs.Length > 0 then Some true else None
        }
        :: this.IpConfigs
        |> List.map (fun ipconfig ->
            // Ensure all IP configs have a subnet IP, defaulting to the one for the VM.
            { ipconfig with
                SubnetName = ipconfig.SubnetName.IfEmpty(subnetId.Name.Value)
            })

    /// Builds NICs for this VM, one for each subnet.
    member private this.buildNics(location, nsgId) =
        // NIC for each distinct subnet
        let ipConfigs = this.buildIpConfigs ()

        let ipConfigsBySubnet =
            ipConfigs |> List.groupBy (fun ipconfig -> ipconfig.SubnetName)

        ipConfigsBySubnet
        |> List.map (fun (subnetName, subnetIpConfigs) ->
            let isPrimaryNic =
                // NIC for the VM's subnet is considered the primary.
                (this.Subnet.resourceId this).Name = subnetName

            {
                Name =
                    //Primary NIC gets the default NicName, others are have -subnetName appended.
                    if isPrimaryNic then
                        this.NicName.Name
                    else
                        ResourceName $"{this.NicName.Name.Value}-{subnetName.Value}"
                Location = location
                EnableAcceleratedNetworking = this.AcceleratedNetworking |> Option.map toBool
                EnableIpForwarding = // IP forwarding optionally enabled on primary NIC only.
                    if isPrimaryNic then
                        this.IpForwarding |> Option.map toBool
                    else
                        None
                IpConfigs = subnetIpConfigs
                Primary =
                    if ipConfigsBySubnet.Length > 1 then // multiple NICs, need to indicate the primary
                        Some isPrimaryNic
                    else
                        None
                VirtualNetwork = this.VNet.toLinkedResource this
                NetworkSecurityGroup = nsgId
                Tags = this.Tags
            })

    interface IBuilder with
        member this.ResourceId = this.ResourceId

        member this.BuildResources location =
            let nsgId = this.NetworkSecurityGroup |> Option.map (fun nsg -> nsg.ResourceId)
            let generatedNics = this.buildNics (location, nsgId)

            [
                // VM itself
                {
                    Name = this.Name
                    AvailabilityZone = this.AvailabilityZone
                    Location = location
                    DiagnosticsEnabled = this.DiagnosticsEnabled
                    StorageAccount = this.DiagnosticsStorageAccount |> Option.map (fun r -> r.resourceId(this).Name)
                    NetworkInterfaceIds = generatedNics |> List.map (fun nic -> networkInterfaces.resourceId nic.Name)
                    Size = this.Size
                    Priority = this.Priority
                    Credentials =
                        match this.Username with
                        | Some username ->
                            {|
                                Username = username
                                Password = SecureParameter this.PasswordParameterArm
                            |}
                        | None -> raiseFarmer $"You must specify a username for virtual machine {this.Name.Value}"
                    CustomData = this.CustomData
                    DisablePasswordAuthentication = this.DisablePasswordAuthentication
                    PublicKeys =
                        if
                            this.DisablePasswordAuthentication.IsSome
                            && this.DisablePasswordAuthentication.Value
                            && this.SshPathAndPublicKeys.IsNone
                        then
                            raiseFarmer
                                $"You must include at least one ssh key when Password Authentication is disabled"
                        else
                            (this.SshPathAndPublicKeys)
                    Identity = this.Identity
                    OsDisk = this.OsDisk
                    DataDisks = this.DataDisks |> Option.defaultValue []
                    Tags = this.Tags
                }

                let subnetId = this.Subnet.resourceId this

                // NICs
                for nic in generatedNics do
                    nic

                // VNET
                match this.VNet with
                | DeployableResource this vnet ->
                    {
                        Name = this.VNet.resourceId(this).Name
                        Location = location
                        AddressSpacePrefixes = [ this.AddressPrefix ]
                        Subnets =
                            [
                                {
                                    Name = subnetId.Name
                                    Prefix = this.SubnetPrefix
                                    VirtualNetwork = Some(Managed vnet)
                                    NetworkSecurityGroup = nsgId |> Option.map (fun x -> Managed x)
                                    Delegations = []
                                    NatGateway = None
                                    ServiceEndpoints = []
                                    AssociatedServiceEndpointPolicies = []
                                    PrivateEndpointNetworkPolicies = None
                                    PrivateLinkServiceNetworkPolicies = None
                                }
                            ]
                        Tags = this.Tags
                    }
                | _ -> ()

                // IP Address
                match this.PublicIp with
                | Some ref ->
                    {
                        Name = (ref.resourceId this).Name
                        Location = location
                        AllocationMethod =
                            match this.IpAllocation with
                            | Some x -> x
                            | None when this.AvailabilityZone.IsSome -> PublicIpAddress.AllocationMethod.Static
                            | None -> PublicIpAddress.AllocationMethod.Dynamic
                        Sku =
                            if this.AvailabilityZone.IsSome then
                                PublicIpAddress.Sku.Standard
                            else
                                PublicIpAddress.Sku.Basic
                        DomainNameLabel = this.DomainNamePrefix
                        Tags = this.Tags
                        AvailabilityZone = this.AvailabilityZone
                    }
                | None -> ()

                // Storage account - optional
                match this.DiagnosticsStorageAccount with
                | Some (DeployableResource this resourceId) ->
                    {
                        Name = Storage.StorageAccountName.Create(resourceId.Name).OkValue
                        Location = location
                        Dependencies = []
                        Sku = Storage.Sku.Standard_LRS
                        NetworkAcls = None
                        StaticWebsite = None
                        EnableHierarchicalNamespace = None
                        MinTlsVersion = None
                        Tags = this.Tags
                        DnsZoneType = None
                        DisablePublicNetworkAccess = None
                        DisableBlobPublicAccess = None
                        DisableSharedKeyAccess = None
                        DefaultToOAuthAuthentication = None
                    }
                | Some _
                | None -> ()

                // Custom Script - optional
                match this.CustomScript, this.CustomScriptFiles with
                | Some script, files ->
                    {
                        Name = this.Name.Map(sprintf "%s-custom-script")
                        Location = location
                        VirtualMachine = this.Name
                        OS =
                            match this.OsDisk with
                            | FromImage (image, _) -> image.OS
                            | _ ->
                                raiseFarmer "Unable to determine OS for custom script when attaching an existing disk"
                        ScriptContents = script
                        FileUris = files
                        Tags = this.Tags
                    }
                | None, [] -> ()
                | None, _ ->
                    raiseFarmer
                        $"You have supplied custom script files {this.CustomScriptFiles} but no script. Custom script files are not automatically executed; you must provide an inline script which acts as a bootstrapper using the custom_script keyword."

                // Azure AD SSH login extension
                match this.AadSshLogin, this.OsDisk with
                | FeatureFlag.Enabled, FromImage (image, _) when
                    image.OS = Linux && this.Identity.SystemAssigned = Disabled
                    ->
                    raiseFarmer
                        "AAD SSH login requires that system assigned identity be enabled on the virtual machine."
                | FeatureFlag.Enabled, FromImage (image, _) when image.OS = Windows ->
                    raiseFarmer "AAD SSH login is only supported for Linux Virtual Machines"
                // Assuming a user that attaches a disk knows to only using this extension for Linux images.
                | FeatureFlag.Enabled, _ ->
                    {
                        AadSshLoginExtension.Location = location
                        VirtualMachine = this.Name
                        Tags = this.Tags
                    }
                | FeatureFlag.Disabled, _ -> ()
            ]

type VirtualMachineBuilder() =
    let automaticPublicIp =
        Derived(fun (config: VmConfig) -> config.DeriveResourceName publicIPAddresses "ip")
        |> AutoGeneratedResource
        |> Some

    member _.Yield _ =
        {
            Name = ResourceName.Empty
            AvailabilityZone = None
            DiagnosticsEnabled = None
            DiagnosticsStorageAccount = None
            Priority = None
            Size = Basic_A0
            Username = None
            PasswordParameter = None
            DataDisks = Some []
            Identity = ManagedIdentity.Empty
            CustomScript = None
            CustomScriptFiles = []
            DomainNamePrefix = None
            CustomData = None
            DisablePasswordAuthentication = None
            SshPathAndPublicKeys = None
            AadSshLogin = FeatureFlag.Disabled
            OsDisk = FromImage(WindowsServer_2012Datacenter, { Size = 128; DiskType = Standard_LRS })
            AddressPrefix = "10.0.0.0/16"
            SubnetPrefix = "10.0.0.0/24"
            VNet = derived (fun config -> config.DeriveResourceName virtualNetworks "vnet")
            Subnet = Derived(fun config -> config.DeriveResourceName subnets "subnet")
            PublicIp = automaticPublicIp
            IpAllocation = None
            AcceleratedNetworking = None
            IpForwarding = None
            IpConfigs = []
            PrivateIpAllocation = None
            LoadBalancerBackendAddressPools = []
            NetworkSecurityGroup = None
            Tags = Map.empty
        }

    member _.Run(state: VmConfig) =
        match state.AcceleratedNetworking with
        | Some (Enabled) ->
            match state.Size with
            | NetworkInterface.AcceleratedNetworkingUnsupported ->
                raiseFarmer $"Accelerated networking unsupported for specified VM size '{state.Size.ArmValue}'."
            | NetworkInterface.AcceleratedNetworkingSupported -> ()
        | _ -> ()

        { state with
            DataDisks =
                state.DataDisks
                |> Option.map (function
                    | [] ->
                        [
                            {
                                Size = 1024
                                DiskType = DiskType.Standard_LRS
                            }
                            |> DataDiskCreateOption.Empty
                        ]
                    | other -> other)
        }

    /// Sets the name of the VM.
    [<CustomOperation "name">]
    member _.Name(state: VmConfig, name) = { state with Name = name }

    member this.Name(state: VmConfig, name) = this.Name(state, ResourceName name)

    [<CustomOperation "add_availability_zone">]
    member _.AddAvailabilityZone(state: VmConfig, az: string) =
        { state with
            AvailabilityZone = Some az
        }

    /// Turns on diagnostics support using an automatically created storage account.
    [<CustomOperation "diagnostics_support">]
    member _.StorageAccountName(state: VmConfig) =
        let storageResourceRef =
            derived (fun config ->
                let name = config.Name.Map(sprintf "%sstorage") |> sanitiseStorage |> ResourceName
                storageAccounts.resourceId name)

        { state with
            DiagnosticsEnabled = Some true
            DiagnosticsStorageAccount = Some storageResourceRef
        }

    /// Turns on diagnostics support using an externally managed storage account.
    [<CustomOperation "diagnostics_support_external">]
    member _.StorageAccountNameExternal(state: VmConfig, name) =
        { state with
            DiagnosticsEnabled = Some true
            DiagnosticsStorageAccount = Some(LinkedResource name)
        }

    /// Turns on diagnostics support using an Azure-managed storage account.
    [<CustomOperation "diagnostics_support_managed">]
    member _.DiagnosticsSupportManagedStorage(state: VmConfig) =
        { state with
            DiagnosticsEnabled = Some true
            DiagnosticsStorageAccount = None
        }

    /// Sets the size of the VM.
    [<CustomOperation "vm_size">]
    member _.VmSize(state: VmConfig, size) = { state with Size = size }

    /// Sets the admin username of the VM (note: the password is supplied as a securestring parameter to the generated ARM template).
    [<CustomOperation "username">]
    member _.Username(state: VmConfig, username) = { state with Username = Some username }

    /// Sets the name of the template parameter which will contain the admin password for this VM. defaults to "password-for-<vmName>"
    [<CustomOperation "password_parameter">]
    member _.PasswordParameter(state: VmConfig, parameterName) =
        { state with
            PasswordParameter = Some parameterName
        }

    /// Sets the operating system of the VM. A set of samples is provided in the `CommonImages` module.
    [<CustomOperation "operating_system">]
    member _.ConfigureOs(state: VmConfig, image) =
        let osDisk =
            match state.OsDisk with
            | FromImage (_, diskInfo) -> FromImage(image, diskInfo)
            | AttachOsDisk _ -> raiseFarmer "Operating system from attached disk will be used"

        { state with OsDisk = osDisk }

    member this.ConfigureOs(state: VmConfig, (os, offer, publisher, sku)) =
        let image =
            {
                OS = os
                Offer = Offer offer
                Publisher = Publisher publisher
                Sku = ImageSku sku
            }

        this.ConfigureOs(state, image)

    /// Sets the size and type of the OS disk for the VM.
    [<CustomOperation "os_disk">]
    member _.OsDisk(state: VmConfig, size, diskType) =
        if diskType = UltraSSD_LRS then
            raiseFarmer "UltraSSD_LRS can only be used for a data disk, not an OS disk."

        let osDisk =
            match state.OsDisk with
            | FromImage (image, diskInfo) ->
                let updatedDiskInfo =
                    { diskInfo with
                        DiskType = diskType
                        Size = size
                    }

                FromImage(image, updatedDiskInfo)
            | AttachOsDisk _ -> state.OsDisk // uses the size and type from the attached disk

        { state with OsDisk = osDisk }

    [<CustomOperation "attach_os_disk">]
    member _.AttachOsDisk(state: VmConfig, os: OS, disk: DiskConfig) =
        { state with
            OsDisk = AttachOsDisk(os, Managed((disk :> IBuilder).ResourceId))
        }

    member _.AttachOsDisk(state: VmConfig, os: OS, diskId: ResourceId) =
        { state with
            OsDisk = AttachOsDisk(os, Managed diskId)
        }

    [<CustomOperation "attach_existing_os_disk">]
    member _.AttachExistingOsDisk(state: VmConfig, os: OS, disk: DiskConfig) =
        { state with
            OsDisk = AttachOsDisk(os, Unmanaged((disk :> IBuilder).ResourceId))
        }

    member _.AttachExistingOsDisk(state: VmConfig, os: OS, diskId: ResourceId) =
        { state with
            OsDisk = AttachOsDisk(os, Unmanaged diskId)
        }

    [<CustomOperation "attach_data_disk">]
    member _.AttachDataDisk(state: VmConfig, diskId: ResourceId) =
        let existingDisks = state.DataDisks

        match existingDisks with
        | Some disks ->
            { state with
                DataDisks = disks @ [ AttachDataDisk(Managed diskId) ] |> Some
            }
        | None ->
            { state with
                DataDisks = [ AttachDataDisk(Managed diskId) ] |> Some
            }

    member this.AttachDataDisk(state: VmConfig, disk: DiskConfig) =
        match disk.Sku with
        | Some (UltraSSD_LRS) ->
            let existingDisks = state.DataDisks
            let diskId = (disk :> IBuilder).ResourceId

            match existingDisks with
            | Some disks ->
                { state with
                    DataDisks = disks @ [ AttachUltra(Managed diskId) ] |> Some
                }
            | None ->
                { state with
                    DataDisks = [ AttachUltra(Managed diskId) ] |> Some
                }
        | _ -> this.AttachDataDisk(state, (disk :> IBuilder).ResourceId)


    [<CustomOperation "attach_existing_data_disk">]
    member _.AttachExistingDataDisk(state: VmConfig, diskId: ResourceId) =
        let existingDisks = state.DataDisks

        match existingDisks with
        | Some disks ->
            { state with
                DataDisks = disks @ [ AttachDataDisk(Unmanaged diskId) ] |> Some
            }
        | None ->
            { state with
                DataDisks = [ AttachDataDisk(Unmanaged diskId) ] |> Some
            }

    member this.AttachExistingDataDisk(state: VmConfig, disk: DiskConfig) =
        match disk.Sku with
        | Some (UltraSSD_LRS) ->
            let existingDisks = state.DataDisks
            let diskId = (disk :> IBuilder).ResourceId

            match existingDisks with
            | Some disks ->
                { state with
                    DataDisks = disks @ [ AttachUltra(Unmanaged diskId) ] |> Some
                }
            | None ->
                { state with
                    DataDisks = [ AttachUltra(Unmanaged diskId) ] |> Some
                }
        | _ -> this.AttachExistingDataDisk(state, (disk :> IBuilder).ResourceId)

    /// Adds a data disk to the VM with a specific size and type.
    [<CustomOperation "add_disk">]
    member _.AddDisk(state: VmConfig, size, diskType) =
        let existingDisks =
            match state.DataDisks with
            | Some disks -> disks
            | None -> []

        { state with
            DataDisks =
                DataDiskCreateOption.Empty { Size = size; DiskType = diskType } :: existingDisks
                |> Some
        }

    /// Provision the VM without generating a data disk (OS-only).
    [<CustomOperation "no_data_disk">]
    member _.NoDataDisk(state: VmConfig) = { state with DataDisks = None }

    /// Sets priority of VMm. Overrides spot_instance.
    [<CustomOperation "priority">]
    member _.Priority(state: VmConfig, priority) =
        match state.Priority with
        | Some priority ->
            raiseFarmer
                $"Priority is already set to {priority}. Only one priority or spot_instance setting per VM is allowed"
        | None -> { state with Priority = Some priority }

    /// Makes VM a spot instance. Overrides priority.
    [<CustomOperation "spot_instance">]
    member _.Spot(state: VmConfig, (evictionPolicy, maxPrice)) : VmConfig =
        match state.Priority with
        | Some priority ->
            raiseFarmer
                $"Priority is already set to {priority}. Only one priority or spot_instance setting per VM is allowed"
        | None ->
            { state with
                Priority = (evictionPolicy, maxPrice) |> Spot |> Some
            }

    member this.Spot(state: VmConfig, evictionPolicy: EvictionPolicy) : VmConfig =
        this.Spot(state, (evictionPolicy, -1m))

    member this.Spot(state: VmConfig, maxPrice) : VmConfig =
        this.Spot(state, (Deallocate, maxPrice))

    /// Adds a SSD data disk to the VM with a specific size.
    [<CustomOperation "add_ssd_disk">]
    member this.AddSsd(state: VmConfig, size) =
        this.AddDisk(state, size, StandardSSD_LRS)

    /// Adds a conventional (non-SSD) data disk to the VM with a specific size.
    [<CustomOperation "add_slow_disk">]
    member this.AddSlowDisk(state: VmConfig, size) = this.AddDisk(state, size, Standard_LRS)

    /// Sets the prefix for the domain name of the VM.
    [<CustomOperation "domain_name_prefix">]
    member _.DomainNamePrefix(state: VmConfig, prefix) =
        { state with DomainNamePrefix = prefix }

    /// Sets the IP address prefix of the VM.
    [<CustomOperation "address_prefix">]
    member _.AddressPrefix(state: VmConfig, prefix) = { state with AddressPrefix = prefix }

    /// Sets the subnet prefix of the VM.
    [<CustomOperation "subnet_prefix">]
    member _.SubnetPrefix(state: VmConfig, prefix) = { state with SubnetPrefix = prefix }

    /// Sets the subnet name of the VM.
    [<CustomOperation "subnet_name">]
    member _.SubnetName(state: VmConfig, name: ResourceName) =
        { state with
            Subnet = Named(subnets.resourceId name)
        }

    member this.SubnetName(state: VmConfig, name) =
        this.SubnetName(state, ResourceName name)

    /// Control accelerated networking for the VM network interfaces
    [<CustomOperation "accelerated_networking">]
    member _.AcceleratedNetworking(state: VmConfig, flag: FeatureFlag) =
        { state with
            AcceleratedNetworking = Some flag
        }

    /// Enable or disable IP forwarding on the primary VM network interface.
    [<CustomOperation "ip_forwarding">]
    member _.IpForwarding(state: VmConfig, flag: FeatureFlag) = { state with IpForwarding = Some flag }

    /// Uses an external VNet instead of creating a new one.
    [<CustomOperation "link_to_vnet">]
    member _.LinkToVNet(state: VmConfig, name: ResourceName) =
        { state with
            VNet = LinkedResource(Managed(virtualNetworks.resourceId name))
        }

    member this.LinkToVNet(state: VmConfig, name) =
        this.LinkToVNet(state, ResourceName name)

    member this.LinkToVNet(state: VmConfig, vnet: Arm.Network.VirtualNetwork) = this.LinkToVNet(state, vnet.Name)
    member this.LinkToVNet(state: VmConfig, vnet: VirtualNetworkConfig) = this.LinkToVNet(state, vnet.Name)

    [<CustomOperation "link_to_unmanaged_vnet">]
    member _.LinkToUnmanagedVNet(state: VmConfig, id: ResourceId) =
        { state with
            VNet = LinkedResource(Unmanaged(id))
        }

    member _.LinkToUnmanagedVNet(state: VmConfig, name: ResourceName) =
        { state with
            VNet = LinkedResource(Unmanaged(virtualNetworks.resourceId name))
        }

    member this.LinkToUnmanagedVNet(state: VmConfig, name) =
        this.LinkToUnmanagedVNet(state, ResourceName name)

    member this.LinkToUnmanagedVNet(state: VmConfig, vnet: Arm.Network.VirtualNetwork) =
        this.LinkToUnmanagedVNet(state, vnet.Name)

    member this.LinkToUnmanagedVNet(state: VmConfig, vnet: VirtualNetworkConfig) =
        this.LinkToUnmanagedVNet(state, vnet.Name)

    /// Adds the VM network interface to a load balancer backend address pool that is deployed with this VM.
    [<CustomOperation "link_to_backend_address_pool">]
    member _.LinkToBackendAddressPool(state: VmConfig, backendResourceId: ResourceId) =
        { state with
            LoadBalancerBackendAddressPools = Managed(backendResourceId) :: state.LoadBalancerBackendAddressPools
        }

    member _.LinkToBackendAddressPool(state: VmConfig, backend: BackendAddressPoolConfig) =
        { state with
            LoadBalancerBackendAddressPools =
                Managed((backend :> IBuilder).ResourceId)
                :: state.LoadBalancerBackendAddressPools
        }

    /// Adds the VM network interface to an existing load balancer backend address pool.
    [<CustomOperation "link_to_unmanaged_backend_address_pool">]
    member _.LinkToExistingBackendAddressPool(state: VmConfig, backendResourceId: ResourceId) =
        { state with
            LoadBalancerBackendAddressPools = Unmanaged(backendResourceId) :: state.LoadBalancerBackendAddressPools
        }

    [<CustomOperation "custom_script">]
    member _.CustomScript(state: VmConfig, script: string) =
        match state.CustomScript with
        | None ->
            { state with
                CustomScript = Some script
            }
        | Some previousScript ->
            let firstScript =
                if script.Length > 10 then
                    script.Substring(0, 10) + "..."
                else
                    script

            let secondScript =
                if previousScript.Length > 10 then
                    previousScript.Substring(0, 10) + "..."
                else
                    previousScript

            raiseFarmer
                $"Only single custom_script execution is supported (and it can contain ARM-expressions). You have to merge your scripts. You have defined multiple custom_script: {firstScript} and {secondScript}"

    [<CustomOperation "custom_script_files">]
    member _.CustomScriptFiles(state: VmConfig, uris: string list) =
        { state with
            CustomScriptFiles = uris |> List.map Uri
        }

    interface ITaggable<VmConfig> with
        member _.Add state tags =
            { state with
                Tags = state.Tags |> Map.merge tags
            }

    interface IIdentity<VmConfig> with
        member _.Add state updater =
            { state with
                Identity = updater state.Identity
            }

    [<CustomOperation "custom_data">]
    member _.CustomData(state: VmConfig, customData: string) =
        { state with
            CustomData = Some customData
        }

    [<CustomOperation "disable_password_authentication">]
    member _.DisablePasswordAuthentication(state: VmConfig, disablePasswordAuthentication: bool) =
        { state with
            DisablePasswordAuthentication = Some disablePasswordAuthentication
        }

    [<CustomOperation "add_authorized_keys">]
    member _.AddAuthorizedKeys(state: VmConfig, sshObjects: (string * string) list) =
        { state with
            SshPathAndPublicKeys = Some sshObjects
        }

    [<CustomOperation "add_authorized_key">]
    member this.AddAuthorizedKey(state: VmConfig, path: string, keyData: string) =
        this.AddAuthorizedKeys(state, [ (path, keyData) ])

    /// Azure AD login extension may be enabled for Linux VM's.
    [<CustomOperation "aad_ssh_login">]
    member this.AadSshLoginEnabled(state: VmConfig, featureFlag: FeatureFlag) =
        { state with AadSshLogin = featureFlag }

    [<CustomOperation "public_ip">]
    /// Set the public IP for this VM
    member _.PublicIp(state: VmConfig, ref: ResourceRef<_> Option) = { state with PublicIp = ref }

    member _.PublicIp(state: VmConfig, ref: ResourceRef<_>) = { state with PublicIp = Some ref }

    member _.PublicIp(state: VmConfig, ref: LinkedResource) =
        { state with
            PublicIp = Some(LinkedResource ref)
        }

    member _.PublicIp(state: VmConfig, _: Automatic) =
        { state with
            PublicIp = automaticPublicIp
        }

    [<CustomOperation "ip_allocation">]
    /// IP allocation method
    member _.IpAllocation(state: VmConfig, ref: PublicIpAddress.AllocationMethod Option) =
        { state with IpAllocation = ref }

    member _.IpAllocation(state: VmConfig, ref: PublicIpAddress.AllocationMethod) =
        { state with IpAllocation = Some ref }

    [<CustomOperation "private_ip_allocation">]
    /// IP allocation method
    member _.PrivateIpAllocation(state: VmConfig, ref: PrivateIpAddress.AllocationMethod Option) =
        { state with PrivateIpAllocation = ref }

    member _.PrivateIpAllocation(state: VmConfig, ref: PrivateIpAddress.AllocationMethod) =
        { state with
            PrivateIpAllocation = Some ref
        }

    [<CustomOperation "add_ip_configurations">]
    /// Add additional IP configurations
    member _.AddIpConfigurations(state: VmConfig, ipConfigs: IpConfiguration list) =
        { state with
            IpConfigs = state.IpConfigs @ ipConfigs
        }

    /// Sets the network security group
    [<CustomOperation "network_security_group">]
    member _.NetworkSecurityGroup(state: VmConfig, nsg: IArmResource) =
        { state with
            NetworkSecurityGroup = Some(Managed nsg.ResourceId)
        }

    member _.NetworkSecurityGroup(state: VmConfig, nsg: ResourceId) =
        { state with
            NetworkSecurityGroup = Some(Managed nsg)
        }

    member _.NetworkSecurityGroup(state: VmConfig, nsg: NsgConfig) =
        { state with
            NetworkSecurityGroup = Some(Managed (nsg :> IBuilder).ResourceId)
        }

    /// Links the VM to an existing network security group.
    [<CustomOperation "link_to_network_security_group">]
    member _.LinkToNetworkSecurityGroup(state: VmConfig, nsg: IArmResource) =
        { state with
            NetworkSecurityGroup = Some(Unmanaged(nsg.ResourceId))
        }

    member _.LinkToNetworkSecurityGroup(state: VmConfig, nsg: ResourceId) =
        { state with
            NetworkSecurityGroup = Some(Unmanaged nsg)
        }

    member _.LinkToNetworkSecurityGroup(state: VmConfig, nsg: NsgConfig) =
        { state with
            NetworkSecurityGroup = Some(Unmanaged (nsg :> IBuilder).ResourceId)
        }


let vm = VirtualMachineBuilder()

type IpConfigBuilder() =
    member _.Yield _ =
        {
            SubnetName = ResourceName.Empty
            PublicIpAddress = None
            LoadBalancerBackendAddressPools = []
            PrivateIpAllocation = None
            Primary = None
        }

    [<CustomOperation "subnet_name">]
    member _.SubnetName(state: IpConfiguration, name: ResourceName) = { state with SubnetName = name }

    [<CustomOperation "public_ip">]
    member _.PublicIp(state: IpConfiguration, ref: LinkedResource) =
        { state with
            PublicIpAddress = Some ref
        }

    [<CustomOperation "link_to_backend_address_pool">]
    member _.LinkToBackendAddressPool(state: IpConfiguration, backendResourceId: ResourceId) =
        { state with
            LoadBalancerBackendAddressPools = Managed(backendResourceId) :: state.LoadBalancerBackendAddressPools
        }

    member _.LinkToBackendAddressPool(state: IpConfiguration, backend: BackendAddressPoolConfig) =
        { state with
            LoadBalancerBackendAddressPools =
                Managed((backend :> IBuilder).ResourceId)
                :: state.LoadBalancerBackendAddressPools
        }

    [<CustomOperation "link_to_unmanaged_backend_address_pool">]
    member _.LinkToExistingBackendAddressPool(state: IpConfiguration, backendResourceId: ResourceId) =
        { state with
            LoadBalancerBackendAddressPools = Unmanaged(backendResourceId) :: state.LoadBalancerBackendAddressPools
        }

    [<CustomOperation "private_ip_allocation">]
    member _.PrivateIpAllocation(state: IpConfiguration, ref: AllocationMethod Option) =
        { state with PrivateIpAllocation = ref }

    member _.PrivateIpAllocation(state: IpConfiguration, ref: AllocationMethod) =
        { state with
            PrivateIpAllocation = Some ref
        }

let ipConfig = IpConfigBuilder()
