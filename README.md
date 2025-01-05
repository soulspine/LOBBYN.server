# LOBBYN SERVER

Opens a websocket listener on port 8080.

First request comes from the Client and has to be an introduction. JSON object with:
- `PlayerName` - summoner name
- `PlayerTagline` - account's tagline
- `PlayerRegion` - one of values included in [this enum](DTOs/PlayerRegion.cs)

After introduction, server will send a number from range `0-29`. Client has to set profile icon of account specified in headers to icon with this id.

Server will wait 30 seconds for message `Verify`. If it doesn't receive it, connection will be closed.

If server receives `Verify` message, it will check if the icon is correct using Riot's External API. If everything is fine, it will send `Verified` as a confirmation. From now on, all messages can be sent freely until the socket is closed.