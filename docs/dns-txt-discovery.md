# DNS TXT endpoint discovery

DNS TXT discovery is the default connection mode for new Configurator setups.
Existing saved connections and preferences keep their selected mode. It does not
change the reporting path:

`verified hook -> Relay -> current-user named pipe -> Client -> Dashboard`

Only the running Client performs ongoing DNS resolution. Hooks never query DNS,
contact Dashboard directly, or start Client. Tray Exit stops managed reporting.
Dashboard remains the sole receiver and does not publish or modify DNS records.

## Record and setup

Publish one TXT record containing the complete root HTTPS Dev Tunnel URL:

| Field | Example |
| --- | --- |
| DNS name | `signaler.cablesorter.io` |
| Type | `TXT` |
| Value | `https://wg012m7b-51820.usw2.devtunnels.ms/` |
| TTL | `600` seconds |

That tunnel URL is an example, not a permanent endpoint. Update the record when
Dashboard's tunnel URL changes. No Azure Storage account, blob, SAS, connection
package, Client Azure account or server-side discovery code is required.

In Configurator's Connection tab, DNS TXT discovery is preselected for new setups
and the editable record hostname defaults to `signaler.cablesorter.io`. Change
the hostname if needed, test the connection, review the settings preview and Apply.
To use a fixed address instead, select Direct Dashboard URL. Existing direct
configurations are not automatically switched. Opening Configurator alone never
applies the default or starts Client.

Configuration v7 stores `connectionMode: "dnsTxtDiscovery"` and `dnsRecordName`.
The resolved URL is runtime state, not a saved fallback. Update Client,
Configurator and Relay together; older releases cannot read this configuration.

## Resolution and failure behavior

Resolution uses system-configured DNS servers rather than a hardcoded public
DNS-over-HTTPS provider. A TXT record may consist of multiple strings; these
strings are joined within that one record. Multiple competing TXT records,
oversized answers, non-HTTPS URLs, credentials, query strings, fragments, non-root
paths and non-Dev-Tunnel hosts are not accepted.

DNS TTL bounds the endpoint lease, with an application maximum of ten minutes.
A `600`-second record is refreshed within that lifetime. A transient lookup
failure cannot extend the original lease of a previously accepted endpoint.
Without a valid endpoint, reporting is suspended rather than using a stale or
unrelated direct address. Lookup and retry work is bounded and cancellable.

Changing the accepted endpoint replaces the reporting generation without
replaying the previous endpoint's buffered conversation data to a new receiver.
Connection testing resolves the address and checks Dashboard health; it does not
start Client, install hooks, or persist the resolved URL.

## Security and removed Azure configuration

DNS records and their tunnel addresses are public. DNS TXT discovery is not
authentication, encryption of the published address, proof of Dashboard ownership,
or a replacement for DNS security. HTTPS protects transport; anonymous Dev Tunnel
sharing still does not authenticate senders. Use a record you control and protect
its DNS administration. Allowing an attacker to change it can redirect reporting
to another otherwise valid Dev Tunnel endpoint.

Azure Blob private discovery has been removed. Former private-Azure connection
configurations are explicitly unsupported, never silently reinterpreted as DNS or
direct mode. Direct-mode configuration versions 1 through 6 remain readable.
Saved Azure credentials, replay files, publisher state and cloud resources are
left untouched and are not read by DNS discovery. Their cleanup or revocation is
a separate authorized operation; removing the feature does not revoke any
previously issued SAS.
