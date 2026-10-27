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

    # proxychains only understands numeric addresses in its ProxyList.
    proxy_ip=$(getent ahostsv4 "$proxy_address" | awk 'NR==1 {print $1}')
    if [ -z "$proxy_ip" ]; then
        echo "Cannot resolve the upstream proxy $proxy_address, MPTCP is used directly" >&2
        return
    fi

    credentials=""
    [ -z "$user" ] || credentials=" $user $password"
    # The MPTCP hop on 1081 requires its own throw-away credentials, so that nothing else on this host can use it.
    # The file contains the proxy password: keep it readable by root only.
    ( umask 077
      cat > /etc/proxychains.conf <<EOF
strict_chain
proxy_dns
tcp_read_time_out 15000
tcp_connect_time_out 8000

[ProxyList]
socks5 127.0.0.1 1081 $INNER_USER $INNER_PASS
socks5 $proxy_ip $proxy_port$credentials
EOF
    )
    chmod 600 /etc/proxychains.conf
    PROXY_ENABLED=1
}

# The local SOCKS server and redsocks require the credentials of the "LocalProxy" section of config.json,
# unless "LocalProxyRequireAuth" is explicitly false (for clients such as browsers that cannot log in to a
# SOCKS proxy). Either way they only listen on the loopback interface. redsocks gets its login through a
# root-only configuration.
config_local_auth() {
    cfg=$(get_config_path)
    if [ -z "$cfg" ] || [ ! -f "$cfg" ]; then
        echo "config.json not found: refusing to start an unauthenticated proxy" >&2
        exit 1
    fi
    LOCAL_USER=""
    LOCAL_PASS=""
    REDSOCKS_LOGIN=""
    if [ "$(jq -r 'if .LocalProxyRequireAuth == false then "false" else "true" end' "$cfg")" = "false" ]; then
        echo "LocalProxyRequireAuth is false: the local proxy on 127.0.0.1:1080 accepts any local client" >&2
    else
        LOCAL_USER=$(jq -r '.LocalProxy.user // empty' "$cfg")
        LOCAL_PASS=$(jq -r '.LocalProxy.password // empty' "$cfg")
        if [ -z "$LOCAL_USER" ] || [ -z "$LOCAL_PASS" ]; then
            echo "LocalProxy.user and LocalProxy.password are required in config.json (or set LocalProxyRequireAuth to false): refusing to start an unauthenticated proxy" >&2
            exit 1
        fi
        case "$LOCAL_USER$LOCAL_PASS" in
            *[[:space:]]*|*[[:cntrl:]]*|*\"*|*\;*|*\{*|*\}*)
                echo "LocalProxy credentials must not contain whitespace, control characters, quotes, ';', '{' or '}'" >&2
                exit 1
                ;;
        esac
        REDSOCKS_LOGIN="    login = \"$LOCAL_USER\";
    password = \"$LOCAL_PASS\";"
    fi
    INNER_USER=inner
    INNER_PASS=$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 32)
    ( umask 077
      cat > /etc/mptcp-redsocks.conf <<EOF
base {
 log_debug = off;
 log_info = on;
 log = "stderr";
 daemon = off;
 redirector = iptables;
}

redsocks {
    local_ip = 0.0.0.0;
    local_port = 12345;

    ip = 127.0.0.1;
    port = 1080;
    type = socks5;
$REDSOCKS_LOGIN
}
EOF
    )
    chmod 600 /etc/mptcp-redsocks.conf
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
    # Windows traffic routed through WSL (tray icon): leave through the bridged interfaces with their address.
    $SUDO iptables -t nat -S POSTROUTING 2>/dev/null | grep -- '--comment mptcp-windows' | sed 's/^-A/-D/' | while read -r rule; do
        # shellcheck disable=SC2086
        $SUDO iptables -t nat $rule
    done
}

config_iptable(){
    $SUDO iptables -t nat -A POSTROUTING -s "$(ip -4 -o addr show dev eth0 | awk '{print $4; exit}')" ! -o eth0 -m comment --comment mptcp-windows -j MASQUERADE
    $SUDO iptables -t nat -N REDSOCKS
    $SUDO iptables -t nat -A REDSOCKS -p tcp -j REDIRECT --to-ports 12345
    $SUDO iptables -t nat -A PREROUTING -p tcp --dport 443 -j REDSOCKS
    $SUDO iptables -t nat -A PREROUTING -p tcp --dport 80 -j REDSOCKS
}

stop_proxy
delete_iptable

$SUDO sysctl -w net.ipv4.ip_forward=1 > /dev/null
config_limits
config_local_auth
config_iptable
config_proxy
redsocks -c /etc/mptcp-redsocks.conf &

# Start the local SOCKS server on 1080 behind the command prefix given as arguments, with or without login.
start_local_socks() {
    if [ -n "$LOCAL_USER" ]; then
        "$@" microsocks -i 127.0.0.1 -p 1080 -u "$LOCAL_USER" -P "$LOCAL_PASS" &
    else
        "$@" microsocks -i 127.0.0.1 -p 1080 &
    fi
}

# The SOCKS servers only listen on the loopback interface: the vNICs are bridged onto the physical
# networks, so binding them to 0.0.0.0 would expose an open proxy to the LAN.
if [ "$PROXY_ENABLED" -eq 1 ]; then
    # redsocks -> 1080 -> (MPTCP socks on 1081) -> upstream proxy
    start_local_socks proxychains
    mptcpize run microsocks -i 127.0.0.1 -p 1081 -u "$INNER_USER" -P "$INNER_PASS" &
else
    start_local_socks mptcpize run
fi

wait
cleanup
