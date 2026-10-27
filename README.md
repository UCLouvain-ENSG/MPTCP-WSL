
# MPTCP Enabler

*This project aims to enable MPTCP on WSL2 by changing the WSL kernel with a MPTCP compatible kernel and by exposing all Windows Network Interfaces to WSL2.*

With this application u will be able to :

1. use all Windows NIC's on WSL2 with proper configuration
2. use a MPTCP-Proxy running on WSL2.
3. or even to route all the Windows traffic into WSL. (The thesis does this by lowering the metric of the WSL network interface in Windows so that it carries the default route; this step is not implemented in the current service, see the Appendix.)
4. use a second proxy to make non compatible distant server compatible.


## Thesis
This project is the implementation part of the master thesis of Bilal Brarou, supervised by Tom Barbette (UCLouvain, 2023-2024): [*Enabling MPTCP on Windows through WSL*](https://thesis.dial.uclouvain.be/server/api/core/bitstreams/9a1aed97-1f94-4503-8459-59c06c59a41a/content). It explains the design choices (chapter 4: exposing the Windows interfaces to WSL2 with one Hyper-V switch per NIC, and mapping Windows and WSL interfaces by MAC address; chapter 5: redsocks and proxy chaining; chapter 6: routing Windows traffic through WSL) and the performance evaluation (chapter 7).

Reference:
> B. Brarou, *Enabling MPTCP on Windows through WSL*, Master's thesis in Computer Science, École polytechnique de Louvain, UCLouvain, 2024. https://thesis.dial.uclouvain.be/server/api/core/bitstreams/9a1aed97-1f94-4503-8459-59c06c59a41a/content

## Requirements
To run properly this application need to have :

- Windows 10/11 x64 Pro (Hyper-V is required)
- Windows Subsystem Linux enabled and updated
- Hyper-V enabled
- An Ubuntu distribution installed in WSL2. It does not need to be the default one. systemd must be enabled in it (`install.sh` tells you how if it is not).

There is nothing else to install on Windows: the service is self-contained, so no .NET runtime is needed.

## Pre-installation setup
Windows will automatically turn off wifi when Ethernet is plugged in. If you want to try MPTCP over Wifi + Ethernet (or 4G through USB, all the same) you must disable this behavior :
1. Open Registry Editor.
2. Go to HKEY_LOCAL_MACHINE\Software\Policies\Microsoft\Windows\WcmSvc\Local.
3. Create/change the fMinimizeConnections registry DWORD to 0.
4. Close Registry Editor and reboot.

## Installation

### Step 1: Windows
Download `mptcp-installer.msi` from the release section of this repo and run it as administrator. It installs and starts the *MPTCP Enabler* service, and from then on the service does the following by itself:

- creates one Hyper-V external switch per physical NIC ("MPTCP - <NIC>") and attaches them all to the WSL2 VM, again each time the VM starts and when a NIC appears;
- creates `config.json` (see below) and adds every new NIC to it;
- sets `kernel=` in your `.wslconfig` to the bundled MPTCP kernel (unless `ManageKernelLocation` is `false`). The new kernel is used after the next `wsl --shutdown`.

The service runs as Local System; it finds your Windows profile on its own (see the Appendix), so there is no need to configure it to run as your user.
![MPTCP Service](https://i.ibb.co/JFnKSyY/Capture-d-cran-2024-03-03-161257.png)

### Step 2: Linux
In the Ubuntu distribution, run `./install.sh` from `mptcp-linux-service`. If systemd is not enabled, it stops and tells you what to add to `/etc/wsl.conf`; run `wsl --shutdown`, then run it again. It installs the packages, the NetworkManager hook that sets up the routing table and MPTCP endpoint of each interface, and the `mptcp-wsl.service` that runs the local SOCKS proxy and redsocks to redirect Windows traffic into a proxy.

### Step 3: Credentials
Edit `config.json` and fill in `LocalProxy` (`user` and `password`). It is required: the local proxy does not start without it. Optionally fill in `Proxy` to chain to a distant MPTCP-compatible proxy. Then run `sudo systemctl restart mptcp-wsl` in the distribution.

Point your Windows application at the SOCKS5 proxy `127.0.0.1:1080` with the `LocalProxy` credentials.

## Configuration file Documentation
After the installation a config file named `config.json` should be created automatically and located at the  
`C:\Users\<USER>\AppData\Local\MPTCP\`folder and is structured as follow :

- **ManageKernelLocation** : If set to true, the application automatically puts the path of the MPTCP compatible kernel (`C:\Program Files (x86)\MPTCP\MPTCP Enabler\bzImage`) in `.wslconfig` and overwrites any other `kernel=` line there. Set it to false to use your own kernel.
- **ManageEndpoint** : if set to true, the endpoint settings will be applied into WSL.
- **Config**: Contains information about each NIC, endpoint parameters can be set on the Types section
- **Proxy**: this section is about using a distant MPTCP-Compatible Proxy. This settings will not be took into account if left blank.
- **LocalProxy**: `user` and `password` required by the local SOCKS5 proxy on `127.0.0.1:1080` (point Windows applications such as Steam at it). `mptcp-wsl.sh` refuses to start the local proxy while these are empty, so the proxy is never exposed unauthenticated.
- **SubflowNr**: This restricts the Multipath TCP connection to use up to  `n` different subflows. Servers should protect themselves by setting this limit to a few subflows. Most use cases would work well with 2 or 4 subflows.
- **AddAddrAcceptedNr**: This parameter limits the number of addresses that are learned over each Multipath TCP connection. This parameter could be used to protect the Multipath TCP implementation against attacks where two many addresses are advertised. Most use cases would work with 4 accepted addresses.

Each endpoints can be configured as follow:
-   `subflow`. When this flag is set, the path manager will try to create a subflow over this interface when a Multipath TCP is created or the interface becomes active while there was an ongoing Multipath TCP connection. This flag is mainly useful for clients.
-   `signal`. When this flag is set, the path manager will announce the address of the endpoint over any Multipath TCP connection created using other addresses. This flag can be used on clients or servers. It is mainly useful on servers that have multiple addresses.
-   `backup`. This flag can be combined with the two other flags. When combined with the  `subflow` flag, it indicates that a backup subflow will be created. When combined with the  `signal` flag, it indicates that the address will be advertised as a backup address.
## Appendix
- The service finds your Windows profile (where `config.json`, the log and `.wslconfig` live) from the second line of `C:\ProgramData\MPTCP\installedOn` (`DOMAIN\user`), or from the interactively logged-on user if that file does not exist. It waits until a profile is found.
- If the `Proxy` section is left blank, no upstream proxy is used and the local SOCKS server uses MPTCP directly. The local SOCKS servers only listen on 127.0.0.1 and always require authentication (`LocalProxy`).
- The WSL2 VM stops when no WSL process is running, and the proxy goes with it. Keep a `wsl` session open (or run a long-lived command such as `wsl -- sleep infinity`) while you use the proxy. The service re-attaches the interfaces every time the VM starts.
- Routing all Windows traffic through WSL (thesis, section 6.2) is not done by the service. It needs the default route of Windows to go through the WSL `eth0` gateway, which means lowering the metric of the WSL network interface itself (not of the route). Until this is automated, point your applications at the SOCKS proxy instead.
- The "MPTCP Enabler" service will create for each NIC an HyperV switch that begin with "MPTCP -", this prefix is important and must not be changed.
- U could use MPTCP on other distribution like Debian or Kali or other since it will share the same kernel, the most important thing is that the `mptcp-wsl.service` is running on a Ubuntu distribution.