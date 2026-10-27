#!/bin/sh

if [ "$(ps -p 1 -o comm=)" != "systemd" ]; then
    echo "systemd is not running in this WSL distribution." >&2
    echo "Add the following to /etc/wsl.conf, run 'wsl --shutdown' from Windows, then run this script again:" >&2
    printf '\n[boot]\nsystemd=true\n\n' >&2
    exit 1
fi

# The files to install are next to this script.
cd "$(dirname "$0")" || exit 1

sudo apt-get update
for package in jq network-manager redsocks microsocks proxychains mptcpize iptables; do
    sudo apt-get install -y "$package" || echo "WARNING: could not install $package" >&2
done

# Install everything before starting the service.
# Explicit modes: this script may run from the Windows drive, where every file looks world-writable, and
# NetworkManager ignores a dispatcher script that anybody can write to.
sudo mkdir -p /usr/local/bin/mptcp
sudo install -m 755 mptcp-wsl.sh /usr/local/bin/mptcp/mptcp-wsl.sh
# Run by the tray icon to apply a change of MPTCP mode (aggregation / fallback) at once.
sudo install -m 755 mptcp-apply-endpoints /usr/local/sbin/mptcp-apply-endpoints
sudo install -m 644 mptcp-redsocks.conf /etc/mptcp-redsocks.conf
sudo install -m 644 mptcp-wsl.service /etc/systemd/system/mptcp-wsl.service

sudo install -m 755 iface_setup /etc/NetworkManager/dispatcher.d/iface_setup
sudo install -m 644 mptcp-NetworkManager.conf /etc/NetworkManager/conf.d/mptcp-NetworkManager.conf

sudo systemctl disable redsocks
sudo systemctl disable mptcp.service
sudo systemctl daemon-reload
sudo systemctl restart NetworkManager
sudo systemctl enable mptcp-wsl.service
sudo systemctl restart mptcp-wsl.service
