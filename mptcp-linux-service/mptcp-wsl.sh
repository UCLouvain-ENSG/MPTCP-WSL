#!/bin/sh

SUDO=""
[ "$(id -u)" -eq 0 ] || SUDO="sudo"

PROXY_ENABLED=0

cleanup(){
    stop_proxy
    delete_iptable
    exit 0
}

trap 'cleanup' INT TERM

# Print the path of config.json of the Windows user, as seen from WSL.
get_config_path() {
    cmd_exe=""
    cmd_drive=""
    for drive in /mnt/*; do
        if [ -x "$drive/Windows/System32/cmd.exe" ]; then
            cmd_exe="$drive/Windows/System32/cmd.exe"
            cmd_drive="$drive"
            break
        fi
    done
    [ -n "$cmd_exe" ] || return 1
    # cd to a Windows drive so that cmd.exe does not complain about a Linux working directory.
    win_profile=$(cd "$cmd_drive" && "$cmd_exe" /c "echo %USERPROFILE%" 2>/dev/null | tr -d '\r')
    [ -n "$win_profile" ] || return 1
    unix_profile=$(wslpath -u "$win_profile") || return 1
    echo "$unix_profile/AppData/Local/MPTCP/config.json"
}

config_limits(){
    cfg=$(get_config_path)
    if [ -z "$cfg" ] || [ ! -f "$cfg" ]; then
        echo "config.json not found, keeping the default MPTCP limits" >&2
        return
    fi
    SubflowNr=$(jq -r '.SubflowNr // empty' "$cfg")
    AddAddrAcceptedNr=$(jq -r '.AddAddrAcceptedNr // empty' "$cfg")
    [ -z "$SubflowNr" ] || $SUDO ip mptcp limits set subflow "$SubflowNr"
    [ -z "$AddAddrAcceptedNr" ] || $SUDO ip mptcp limits set add_addr_accepted "$AddAddrAcceptedNr"
}

# Write the proxychains configuration, or leave PROXY_ENABLED=0 when no upstream proxy is configured.
config_proxy() {
    PROXY_ENABLED=0
    cfg=$(get_config_path)
    if [ -z "$cfg" ] || [ ! -f "$cfg" ]; then
        echo "config.json not found, no upstream proxy" >&2
        return
    fi
    proxy_address=$(jq -r '.Proxy.proxyAddress // empty' "$cfg")
    proxy_port=$(jq -r '.Proxy.proxyPort // empty' "$cfg")
    user=$(jq -r '.Proxy.user // empty' "$cfg")
    password=$(jq -r '.Proxy.password // empty' "$cfg")

    if [ -z "$proxy_address" ] || [ -z "$proxy_port" ]; then
        echo "No upstream proxy configured, MPTCP is used directly" >&2
        return
    fi
    # proxychains is whitespace separated: refuse values that would add or alter entries.
    case "$proxy_address$proxy_port$user$password" in
        *[[:space:]]*|*[[:cntrl:]]*)
            echo "Ignoring the upstream proxy: whitespace or control characters in its settings" >&2
            return
            ;;
    esac

    credentials=""
    [ -z "$user" ] || credentials=" $user $password"
    # The file contains the proxy password: keep it readable by root only.
    ( umask 077
      cat > /etc/proxychains.conf <<EOF
strict_chain
proxy_dns
tcp_read_time_out 15000
tcp_connect_time_out 8000

[ProxyList]
socks5 127.0.0.1 1081
socks5 $proxy_address $proxy_port$credentials
EOF
    )
    chmod 600 /etc/proxychains.conf
    PROXY_ENABLED=1
}

stop_proxy(){
    $SUDO pkill -f "redsocks -c /etc/mptcp-redsocks.conf"
    $SUDO pkill -f "microsocks -i 127.0.0.1 -p 108[01]"
}

delete_iptable(){
    $SUDO iptables -t nat -D PREROUTING -p tcp --dport 443 -j REDSOCKS 2>/dev/null
    $SUDO iptables -t nat -D PREROUTING -p tcp --dport 80 -j REDSOCKS 2>/dev/null
    $SUDO iptables -t nat -F REDSOCKS 2>/dev/null
    $SUDO iptables -t nat -X REDSOCKS 2>/dev/null
}

config_iptable(){
    $SUDO iptables -t nat -N REDSOCKS
    $SUDO iptables -t nat -A REDSOCKS -p tcp -j REDIRECT --to-ports 12345
    $SUDO iptables -t nat -A PREROUTING -p tcp --dport 443 -j REDSOCKS
    $SUDO iptables -t nat -A PREROUTING -p tcp --dport 80 -j REDSOCKS
}

stop_proxy
delete_iptable

$SUDO sysctl -w net.ipv4.ip_forward=1 > /dev/null
config_limits
config_iptable
config_proxy
redsocks -c /etc/mptcp-redsocks.conf &

# The SOCKS servers only listen on the loopback interface: the vNICs are bridged onto the physical
# networks, so binding them to 0.0.0.0 would expose an open, unauthenticated proxy to the LAN.
if [ "$PROXY_ENABLED" -eq 1 ]; then
    # redsocks -> 1080 -> (MPTCP socks on 1081) -> upstream proxy
    proxychains microsocks -i 127.0.0.1 -p 1080 &
    mptcpize run microsocks -i 127.0.0.1 -p 1081 &
else
    mptcpize run microsocks -i 127.0.0.1 -p 1080 &
fi

wait
cleanup
