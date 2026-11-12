#!/usr/bin/env bash
# box.sh up|down
#
# A stand-in for the scorer's box on one Linux machine: a network namespace "box" joined to this one by a veth pair,
# so the entry's packets cross a device and its own TCP stack instead of the loopback shortcut.
#  - The namespace gets its own table of established connections with 16,384 buckets, the size a 2 GB machine boots
#    with (tcp_child_ehash_entries; Linux 6.1+). With hundreds of thousands of sockets every packet then walks a
#    chain, as on the scorer's box.
#  - What arrives for the box is handled on CPU 0, where the entry runs (receive packet steering on its end of the
#    pair), and what arrives for the load generator on the other CPUs.
#  - No connection tracking on the pair, as the scorer's box has none.
# The box is 10.9.0.1; the load generator's sockets bind 10.9.1.1 to 10.9.1.32.
set -euo pipefail
down() {
  sudo ip netns del box 2>/dev/null || true
  sudo ip link del vlg 2>/dev/null || true
  for chain in PREROUTING OUTPUT; do sudo iptables -t raw -D $chain -j XLAB 2>/dev/null || true; done
  sudo iptables -t raw -F XLAB 2>/dev/null || true
  sudo iptables -t raw -X XLAB 2>/dev/null || true
}
case "${1:-}" in
  down) down ;;
  up)
    down
    sudo sysctl -q -w net.ipv4.tcp_child_ehash_entries=16384
    sudo ip netns add box
    sudo ip link add vlg type veth peer name vbox
    sudo ip link set vbox netns box
    sudo ip addr add 10.9.0.2/16 dev vlg
    for i in $(seq 1 32); do sudo ip addr add "10.9.1.$i/16" dev vlg; done
    sudo ip link set vlg up
    sudo ip netns exec box ip addr add 10.9.0.1/16 dev vbox
    sudo ip netns exec box ip link set vbox up
    sudo ip netns exec box ip link set lo up
    cpus=$(nproc)
    others=$(printf '%x' $(( (1 << cpus) - 2 )))
    sudo ip netns exec box bash -c 'echo 1 > /sys/class/net/vbox/queues/rx-0/rps_cpus'
    sudo bash -c "echo $others > /sys/class/net/vlg/queues/rx-0/rps_cpus"
    sudo iptables -t raw -N XLAB
    sudo iptables -t raw -A XLAB -i vlg -j NOTRACK
    sudo iptables -t raw -A XLAB -o vlg -j NOTRACK
    sudo iptables -t raw -I PREROUTING -j XLAB
    sudo iptables -t raw -I OUTPUT -j XLAB
    # more ephemeral ports for each source address, and room for the load generator's half of every connection
    sudo sysctl -q -w net.ipv4.ip_local_port_range="1024 65535"
    echo "box: 10.9.0.1, ehash $(sudo ip netns exec box sysctl -n net.ipv4.tcp_ehash_entries) buckets, rps box=1 load=$others"
    ping -c 1 -W 2 10.9.0.1 > /dev/null && echo "box: reachable"
    ;;
  *) echo "usage: box.sh up|down"; exit 2 ;;
esac
