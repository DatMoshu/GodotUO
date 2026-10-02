# Hosting packs

A catalogue is static files: `index.json`, `index.json.sig`, the pack ZIPs and
their previews. Any HTTPS host that serves files and byte ranges can host
packs. This page covers the official mirror on the owner's server, and how a
community site mirrors the same catalogue.

## What is served

```
/                    index.json, index.json.sig   (the official catalogue's, copied)
/packs/<id>/<version>.zip
/previews/<id>/<version>/<file>
```

The files are immutable once published. The web server only reads them. A
sync job writes them from the catalogue repository's release.

## Reaching a home server from the internet

A static IP from a residential ISP is uncommon. Most "static" home addresses
are long DHCP leases that change rarely. Either way, three things decide the
setup:

1. **Upload speed is the real limit.** Home plans upload far slower than they
   download. At 35 Mbit/s up, one 512 MB pack takes about two minutes for one
   player, and ten players at once share that. So the home server is the
   **origin and archive**. Popular packs are also listed with a second URL on
   a host with more bandwidth: GitHub release assets on the catalogue
   repository, or a community mirror. The signed index lists every URL, and
   the client tries them in order.
2. **The ISP's terms.** Check whether the residential plan's acceptable-use
   policy allows running a public server. A business plan allows it and comes
   with a real static IP.
3. **What is exposed.** Only HTTPS on port 443, to a web server that serves
   read-only files. Nothing administrative is reachable from the internet.

### Option A: port forwarding (recommended for the pack files)

- On the router, forward TCP 443 (and 80, if the certificate uses the HTTP
  challenge) to the server's LAN address. Give the server a reserved LAN
  address in the router's DHCP settings.
- Point a DNS name at the public IP, for example `packs.<your domain>`. If
  the IP can change, run a dynamic DNS updater on the server.
- Run Caddy, which gets and renews the TLS certificate itself:

  ```
  packs.example.org {
      root * /srv/guo-packs
      file_server
      header /packs/* Cache-Control "public, max-age=31536000, immutable"
      header /index.json* Cache-Control "no-cache"
  }
  ```

  Caddy's `file_server` serves byte ranges, which resumable installs use.
- Exposes the home IP address. Put the server on its own VLAN or the
  router's DMZ segment if the router supports it, so a compromise of the web
  server does not reach the rest of the house.

### Option B: Cloudflare Tunnel

- No open ports, and the home IP stays hidden. Good for the small files
  (`index.json`, previews) and for a status page.
- Cloudflare's self-serve CDN terms restrict serving large files and video
  through the CDN. Pack ZIPs up to 512 MB fit that description. Use option A
  or another host for the ZIPs.

### Administration

Never expose an admin port. Reach the server's admin tools from home, or from
outside over a VPN such as WireGuard or Tailscale. GUO's admin windows talk
to the server the same way (ADR-0026 section 8).

## RAID is not a backup

RAID 10 survives a disk failure. It does not survive deletion, ransomware or
a failed controller. The official packs can always be rebuilt from the
catalogue repository and their release assets. Anything only on the server,
such as logs or a private catalogue, needs its own backup.

## Mirroring as a community host

A community site mirrors the official catalogue by copying `packs/` and
`previews/`. It then asks for its URL to be added to each pack's `urls` in
the catalogue repository. The hashes in the signed index make a mirror safe
to use without trusting the mirror. A site can also run its own catalogue
with its own key, and players add it in the Store.
