# Pinned .nupkg for the IL sshd (M97)

Fetched 2026-11-26 from api.nuget.org with owner approval (the four packages listed in
`docs/MQTT-DESIGN.md` §Transport). The binaries themselves are not committed; re-fetch and
compare sha512 before use. `FxSsh` is kept as a measured negative result: `net8.0` only,
so it cannot load on the set's .NET 6.0.9 host.

| package | bytes | sha512 |
| --- | --- | --- |
| `fxssh.1.4.0.nupkg` | 69130 | `5969b5184d3630d0f6916e997d5adbe77158b7101564e6e64cd51d43839b977b51b45ff0d35e2d0df371b13eac8a2e7a8690db83c8b3531ae8f59d541effc3ce` |
| `microsoft.devtunnels.ssh.3.12.42.nupkg` | 360582 | `ed509d0d34fdef67fe3d609e257d28f7743562a3438ed46b6b962b61858cc5fa5dc6b2680a42caba76ebe32d1808f360ee83118e6fe4863fb3ba343d7ebf80c5` |
| `microsoft.devtunnels.ssh.keys.3.12.42.nupkg` | 100712 | `363c7a424edfd3e5a89c4188749030dedaceeded839b89d28d5c19350f6b30bda8871556958a8f47fdc57cfe237f399e64098fb16b331369311bb314c2cb83e8` |
| `microsoft.devtunnels.ssh.tcp.3.12.42.nupkg` | 129359 | `e6ce5f681a8972dc5fcea70abf8517d0bef8a80e675a39d09f5d892dc98cb685f4d7aec0443e5d2bd85affe38d8ae02b6dd4803bfa3aeb17eec537d9e3767f9b` |

Source URLs (flat container):

- https://api.nuget.org/v3-flatcontainer/fxssh.1.4/0/fxssh.1.4.0.nupkg
- https://api.nuget.org/v3-flatcontainer/microsoft.devtunnels.ssh.3.12/42/microsoft.devtunnels.ssh.3.12.42.nupkg
- https://api.nuget.org/v3-flatcontainer/microsoft.devtunnels.ssh.keys.3.12/42/microsoft.devtunnels.ssh.keys.3.12.42.nupkg
- https://api.nuget.org/v3-flatcontainer/microsoft.devtunnels.ssh.tcp.3.12/42/microsoft.devtunnels.ssh.tcp.3.12.42.nupkg
