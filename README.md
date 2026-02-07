# SrsProxy

Lightweight SMTP relay that applies [Sender Rewriting Scheme (SRS)](https://en.wikipedia.org/wiki/Sender_Rewriting_Scheme) to forwarded emails so they pass SPF checks at the destination.

## Problem

When a mail server forwards emails from external senders to another provider (e.g. Gmail), the destination checks SPF against the **original** sender's domain. Since the forwarding server's IP isn't in that domain's SPF record, the check fails and the message gets rejected.

SRS rewrites the envelope sender (`MAIL FROM`) to use the forwarding domain, so SPF is checked against a domain whose SPF record **does** include the forwarding server.

## How it works

```
MTA --route--> localhost:2525 (SrsProxy) --SRS rewrite--> recipient MX
```

1. Your mail server routes outbound/forwarded mail to SrsProxy on port 2525
2. SrsProxy rewrites the envelope sender: `user@external.com` becomes `SRS0=hash=tt=external.com=user@yourdomain.com`
3. SrsProxy resolves the recipient's MX record and delivers directly via STARTTLS
4. The destination checks SPF against `yourdomain.com` — passes because your server's IP is in the SPF record

The message body and headers are preserved untouched — only the SMTP envelope sender changes.

## Requirements

- The forwarding server's IP must be in the SRS domain's SPF record
- [.NET 10](https://dotnet.microsoft.com/) runtime — or download a self-contained build from [Releases](https://github.com/Licho1/SrsProxy/releases) (no runtime needed)

## Quick start

**Option A — Download a release:**

Download the zip for your platform from [Releases](https://github.com/Licho1/SrsProxy/releases), extract it, copy `appsettings.example.json` to `appsettings.json`, edit it, and run.

**Option B — Build from source:**

```bash
git clone https://github.com/Licho1/SrsProxy.git
cd SrsProxy
cp appsettings.example.json appsettings.json
# Edit appsettings.json — set SrsDomain, SrsSecret, and LocalDomains
dotnet run
```

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `ListenPort` | `2525` | SMTP port to listen on |
| `LocalOnly` | `true` | `true` = bind to 127.0.0.1, `false` = bind to 0.0.0.0 |
| `SrsDomain` | — | Your domain used for rewritten envelope senders |
| `SrsSecret` | — | Secret key for HMAC hash in SRS addresses (any random string) |
| `LocalDomains` | `[]` | Domains that should **not** be SRS-rewritten (your own domains) |

## Installing as a service

### Windows Service

```powershell
# Build (or extract release zip to C:\SrsProxy)
dotnet publish -c Release -o C:\SrsProxy

# Copy and edit config
copy appsettings.example.json C:\SrsProxy\appsettings.json
notepad C:\SrsProxy\appsettings.json

# Install and start
sc create SrsProxy binPath="C:\SrsProxy\SrsProxy.exe" obj="NT AUTHORITY\NetworkService" start=auto
sc start SrsProxy

# Check status
sc query SrsProxy

# View logs in Event Viewer → Windows Logs → Application → Source: SrsProxy
```

To uninstall:
```powershell
sc stop SrsProxy
sc delete SrsProxy
```

### Linux (systemd)

```bash
# Build (or extract release zip to /opt/srsproxy)
dotnet publish -c Release -o /opt/srsproxy

# Copy and edit config
cp appsettings.example.json /opt/srsproxy/appsettings.json
nano /opt/srsproxy/appsettings.json

# Create systemd unit
sudo tee /etc/systemd/system/srsproxy.service << 'EOF'
[Unit]
Description=SRS Proxy SMTP Relay
After=network.target

[Service]
Type=notify
ExecStart=/opt/srsproxy/SrsProxy
WorkingDirectory=/opt/srsproxy
Restart=on-failure

[Install]
WantedBy=multi-user.target
EOF

# Enable and start
sudo systemctl enable --now srsproxy

# Check status / logs
systemctl status srsproxy
journalctl -u srsproxy -f
```

## Testing

```powershell
Send-MailMessage -From "test@external.com" -To "you@gmail.com" -Subject "SRS Test" -Body "test" -SmtpServer 127.0.0.1 -Port 2525
```

Check Gmail → Show original → look for `spf=pass` and `Return-Path: SRS0=...@yourdomain.com`.

## Mail server configuration

### hMailServer

1. Settings → Routes → Add
2. Domain: the destination domain (e.g. `gmail.com`)
3. Target SMTP host: `127.0.0.1`, port: `2525`
4. Uncheck "Server requires authentication"

### Postfix

```
transport_maps = hash:/etc/postfix/transport
```

In `/etc/postfix/transport`:
```
gmail.com   smtp:[127.0.0.1]:2525
```

## Why you need this

If you run a mail server that accepts mail for multiple domains and forwards it to Gmail (or Outlook, etc.), you've probably seen rejections like:

> 550 5.7.26 This mail has been blocked because the sender is unauthenticated. Gmail requires all senders to authenticate with either SPF or DKIM.

The root cause: when you forward `alice@randomsender.com` → `you@gmail.com`, Gmail checks SPF for `randomsender.com`. Your server's IP isn't in their SPF record, so it fails. If `randomsender.com` also lacks DKIM (many smaller domains don't set it up), there's no way for the message to pass authentication — Gmail rejects it.

**SRS fixes the SPF side of this.** By rewriting the envelope sender to `SRS0=...@yourdomain.com`, Gmail checks SPF against *your* domain instead. Since your server's IP is in your own SPF record, it passes.

This is especially common when:
- You host email for several vanity/small domains and forward everything to one Gmail inbox
- Senders don't have DKIM configured (newsletters, small businesses, legacy systems)
- Gmail/Google Workspace tightened authentication requirements (2024+)
- You use hMailServer, Postfix, or similar with forwarding rules

SRS is the standard solution used by large forwarders (pobox.com, university mail systems, etc.) and is defined in [RFC 5765 (informational)](https://datatracker.ietf.org/doc/html/rfc5765).

## How SRS addresses work

```
SRS0=HHHH=TT=originaldomain.com=user@yourdomain.com
     │    │  │                   │
     │    │  │                   └─ original local part
     │    │  └─ original domain
     │    └─ 2-char base32 timestamp (days mod 1024)
     └─ 4-char HMAC-SHA1 hash (prevents forgery)
```

The hash ensures only your server can create valid SRS addresses, preventing abuse of your domain as a bounce relay.

## License

MIT
