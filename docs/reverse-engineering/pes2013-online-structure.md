# PES 2013 PC: estructura general del online

Guía para seguir trabajando sobre el modo online reconstruido de PES 2013. Resume cómo el cliente
usa el online, dónde vive cada pieza en este repo y cómo se investiga lo que falta. Lo que no está
confirmado contra un cliente real se marca con **(sin confirmar)**.

Complementa a `pes2011-session-protocol.md` y `pes2011-cmd-reference.md` (el protocolo base es el
mismo desde PES 2010) y al README (puertos, hosts, certificados).

## 1. Fuentes de verdad

| Fuente | Qué es | Dónde |
|---|---|---|
| Decompilado de `pes2013.exe` | Pseudo-C de las 56.886 funciones del exe ya descifrado en memoria | `C:\VSS\Pes2013Decompilado\export\all_functions.c` (74 MB) |
| Exports dirigidos | Funciones de avatar/URL/HTTP con sus llamadas internas | `export\targeted.c`, `targeted_deep.c`, `bystring.c` |
| Índices de strings | Qué función usa qué string (`urltype`, `CMD_*`...) | `export\string_xrefs.txt`, `bystring_xrefs.txt` |
| Proyecto Ghidra | Proyecto analizado (`Pes2013Dump`) | `C:\VSS\Pes2013Decompilado\project\` |
| Volcado del exe | Memoria del proceso, con `.text` descifrado | `input\pes2013_dump.exe` |
| Código del servidor | Handlers, estado, endpoints HTTP | este repo, `src/OpenEleven.Server` |
| Log de red | Tráfico real de los jugadores | `C:\Servers\PES2013\logs\server.log` |

Los nombres `FUN_xxxxxxxx` de los comentarios del código son direcciones del volcado (VA = dirección
en Ghidra). Para buscar una función: `Select-String -Path all_functions.c -Pattern "FUN_0058a290"`.

### Por qué hubo que volcar la memoria

`pes2013.exe` está protegido: `.text` cifrado (entropía 8), secciones `PSFD00`, `.SIAUJzi`,
`.lLQZltY`, `.OUOjziG` y `rld.dll`. Un import estático solo ve ~56 funciones del cargador. Se
descifra al ejecutarse; por eso se usa el volcado. Para rehacerlo: abrir el juego y correr como
administrador `C:\VSS\PES2013Server\dump_pes2013.bat` (el juego corre elevado y protegido; sin
admin falla con error 5). Después reimportar en Ghidra.

### Cómo volver a decompilar

- Ghidra 12.2 y JDK 25 están en `C:\VSS\Pes2013Decompilado\tools\`. Ghidra necesita JDK 25+ y
  `-Dapplication.settingsdir=...` (ya está en `launch.properties`) para evitar un bug de Felix/OSGi.
- Headless con `GHIDRA_HEADLESS_MAXMEM=10G` (con el límite por defecto se queda sin memoria).
- Scripts en `scripts\`: `ExportAll.java` (todo), `ExportByString.java` + `needles.txt` (funciones que
  usan ciertos strings y quienes las llaman), `ExportTargetedDeep.java`.
- El export completo tarda ~1 h. Un `-process` sobre el proyecto bloquea el proyecto: no abrir la GUI
  a la vez.

## 2. Las tres vías de comunicación del cliente

1. **Sockets TCP de juego** (mensajes `clave=valor`, cifrados): un listener por servicio.
2. **HTTP/HTTPS** (WinHttp, "NTL HttpModule/0.1"): URLs que anuncia el servidor.
3. **UDP**: STUN (3478/3479) para hallar la dirección pública, y P2P entre jugadores para el partido.

### 2.1 Sockets de juego

Seis servicios por título (puertos de PES 2013 en `conf/server.pes2013pc.yaml`):

| Rol | Puerto | Para qué |
|---|---|---|
| Gate | 15010 (fijo en el exe) | Descubrimiento: lista de servidores, URLs, hora, EULA |
| FdLobby | 28041 | Lobby de amistosos/FD |
| Lobby | 28042 | Bloques y salas |
| Menu | 28043 | Menús, comunidades, amigos, rankings, correo |
| Account | 28044 | Login, jugadores (perfiles) |
| VdpChat | 28045 | Chat |

El Gate solo anuncia los demás puertos (`CMD_GET_SVRLIST`, claves `svrgid`/`svrtype`), así que
solo el 15010 está fijo. Cadena de procesamiento: `XorCipher → PacketCodec → BlowfishEcb → KvReader →
CommandDispatcher → [Command] handler` (README, sección Architecture).

### 2.2 Canal HTTP

El cliente **no conoce ninguna URL**: se las da `CMD_GET_URLLIST` (`GateHandlers.GetUrlList`) en una
lista `ulist[]` con `type`, `url`, `version`, `file_size`, `md5`.

- El cliente convierte el `type` a un índice con una tabla interna de 98 nombres
  (`FUN_0054fdc0` → `FUN_010f97b0(0x62, type)`; un tipo desconocido cae al índice `0x2e`).
- Para un comando HTTP, el mensaje lleva `urltype="..."` y `FUN_005af840` le agrega `url="<url>"`
  buscando por ese tipo. **Si el tipo no fue anunciado, no se hace ninguna petición**. Esa fue la causa
  de que no cargaran las fotos de perfil y de que fallara la lista de nicknames.
- Transporte (`FUN_00d11df0`): POST `application/x-www-form-urlencoded; charset="utf-8"`. Un fallo de
  red vuelve al juego como `result="ERR_CLIENT_SENDFAIL", read_buffer=""`. El cuerpo de la respuesta
  llega envuelto como `read_buffer`.
- La ranura 0 de `ulist` siempre se lee como tipo 0 = RANK (regla de PES 2010/2011, **sin confirmar** en
  2013); por eso `GetUrlList` pone RANK primero.
- Con HTTPS el cliente valida el certificado con WinInet: `AdvertiseIp` debe ser un nombre cubierto
  por el certificado. Con `Https.Enabled: false` se sirve HTTP plano en el puerto 80.

URLs que el servidor anuncia hoy para PES 2013 (`GateHandlers.GetUrlList`) y su ruta
(`Web/GameEndpoints.cs`):

| Tipo | Ruta | Contenido |
|---|---|---|
| RANK, RANK_WEEK | `/ranking` | Rankings (`RankingQuery`) |
| PC_SPEC | `/pcspec.bin` | Archivo estático de especificaciones |
| KONAMIID | `/register` | Registro |
| GAMEID_AUTH | `/gameid_auth` | Autenticación por ticket |
| EULA_PES | `/eula_pes.txt?lang=XX` | Términos, por idioma |
| COMMU_MEMBERS_VS, COMMU_TEAM_VS | `/commu_members_vs?`, `/commu_team_vs?` | Cara a cara de una comunidad (terminan en `?`) |
| AVATAR | `/avatar` | Foto de otro jugador (`CMD_GET_AVATAR_HTTP`) |
| NICKNAME | `/nickname` | Catálogo de nicknames (`CMD_GET_NICKNAME_COMPE`) |
| DOWNLOAD y otros | configurables en `Server:Urls` | Manifiesto de DLC, archivos |

El cliente conoce más tipos que no anunciamos (por ejemplo `COMMU_STATS`, `ML_RANKING`,
`ML_RANKING_BYID`, `ML_DIVISIONRANKING`, `ML_GAINRANKING`, `ML_POPULARRANKING`, `ML_PRICERANKING`,
`ML_RIVAL_RANK`, `ML_GAMEPLAYERHISTORY`, `ML_MARKET_PLAYER_PARAM_SIZE`, `WIDGET`, `TOPMENU`, `RSS`).
Son el modo Master League online. Para cada uno que se quiera soportar: buscar su `urltype="..."` en
`string_xrefs.txt`, ver el comando asociado en `all_functions.c`, anunciar el tipo y agregar la ruta.

### 2.3 UDP, STUN y P2P

- STUN (RFC 3489) en UDP 3478/3479. Opciones `Server:Stun:PublicAddress` y `BindAddress` para jugadores
  que llegan por otra red (Radmin/Hamachi, LAN).
- Los partidos son **P2P**. El servidor solo intercambia las direcciones: cada cliente informa su NAT
  con `CMD_SEND_IPANDPORT_NTL` (`ntl_nat_type`, `host_addr`, `reflexive_addr`, `relayed_addr`, con
  `address_family` y `sa_data`) y recibe las de los demás con `CMD_GET_IPANDPORT_NTL`,
  `CMD_WATCH_IPANDPORT_NTL` y `roomplayer_list`. `NtlAddress` corrige direcciones vacías o privadas con
  la que ve el servidor. Si un jugador no se conecta con otro, el problema suele estar acá.
- Si la sala no tiene a los pares con direcciones válidas, el cliente abandona la sala a los ~40 s
  con `CMD_LEAVE_ROOM`.

## 3. Ciclo de vida de una sesión

`SessionState` (`State/SessionState.cs`) y los comandos que lo hacen avanzar:

| Estado | Cómo se llega | Handler |
|---|---|---|
| Connected | Conectar al Gate | `GateHandlers` (`CMD_GET_SVRLIST`, `CMD_GET_URLLIST`, `CMD_GET_SERVER_ENV`...) |
| EulaAccepted | `CMD_GET_EULA` | `GateHandlers` |
| Challenged | `MSG_REQCCODE` | `AccountHandlers` |
| Authenticated | `MSG_REQAUTH` (el login HTTP/ticket ya validó la cuenta) | `AccountHandlers`, `Gate2011Handlers` (`CMD_AUTH_GAMEID`, `CMD_LOGIN`) |
| PlayerSelected | `CMD_SET_CURRENTPLAYER` (luego `CMD_GET_PLAYERLIST`/`CMD_CREATE_PLAYER`) | `AccountHandlers` |
| InBlock | `CMD_GET_LOBBYLIST` → `CMD_JOIN_LOBBY` → `CMD_GET_BLOCKLIST` → `CMD_JOIN_BLOCK` | `ModernLobbyHandlers`, `LobbyHandlers` |
| InRoom | `CMD_CREATEJOIN_ROOM` / `CMD_JOIN_ROOM` | `RoomHandlers` |
| Matching | Partida rápida: `CMD_SEND_QUICK_GAMEBEGINNING_HOPE` | `QuickMatchHandlers` |
| InMatch | `CMD_ENTRY_GAME` → partido | `RoomHandlers`, `MatchHandlers` |

La secuencia de arranque del cliente está en `FUN_00590850` (`CMD_GET_HTTP`, `CMD_GET_URLLIST`...).
Con `EnforceSessionState: true` un comando fuera de estado se rechaza.

## 4. Mapa de handlers

| Archivo (`Handlers/`) | Función |
|---|---|
| `GateHandlers`, `Gate2011Handlers` | Descubrimiento, EULA, URLs, información, login |
| `AccountHandlers` | Autenticación, perfiles, `CMD_SET_PLAYERPROFILE`, game plan |
| `SessionHandlers` | Latidos, idioma |
| `ModernLobbyHandlers`, `LobbyHandlers` | Lobbies, bloques, listas observadas (`CMD_REQWATCHL_*`), envío de IP |
| `RoomHandlers` | Salas, entrada a partido, `CMD_WATCH_*` de la sala |
| `QuickMatchHandlers` | Partida rápida |
| `MatchHandlers` | Resultados, puntaje, faltas, abandono, rating |
| `RankHandlers` | Ranking, divisiones, info privada |
| `CommunityHandlers` | Comunidades, ligas, partidos de comunidad |
| `CompetitionHandlers` | Competiciones configurables |
| `FriendHandlers`, `MailHandlers`, `TextChatHandlers` | Social |
| `PlayerDirectoryHandlers` | Búsqueda de jugadores |
| `AvatarHandlers` | Fotos (`CMD_SET_AVATAR`; lectura por HTTP) |
| `NicknameHandlers` | Nicknames (`CMD_SET_NICKNAME`, `CMD_GET_UNLOCKEDNICKNAMELIST`, catálogo HTTP) |
| `PlayerPresenter` + `State/ListWatchEntries` | Arman los registros de jugador que viajan en las listas |

Los comandos se registran con `[Command("CMD_X", Roles = ..., Profiles = ..., RequiredState = ...)]`;
el descubrimiento es por reflexión (no hay registro manual). `Profiles` limita por título
(`GameProfiles.Since2012`, `Modern`...). La respuesta es un `KvMessage[]` al que llama; para empujar a
otros (salas, listas observadas) se usa `Hub` (`PushWatch`, `BroadcastToRoom`).

### Listas observadas

El cliente se suscribe con `CMD_REQWATCHL_*` y después el servidor empuja `ADD`/`UPDATE`/`DEL`. Las
claves de cada registro salen de **tablas de claves del exe** (no de las respuestas del 2011), y un
registro al que le falta una clave se descarta entero. Claves de PES 2013, verificadas en las strings:

- `roomplayer_list`: `pid, name, room_player_id, host_addr, reflexive_addr, relayed_addr, ntl_nat_type,
  nickname_id, contribution_prize, team_match_nice_num, evaluation_average, stats, game_entry_no,
  is_team_data_load, side`.
- `blockplayer_list`: `pid, name, skill_level, nickname_id, room_id, rating, kind, lang, npid, xuid,
  is_restricted`. `skill_level` usa el enum `BEGINNER, AMATEUR, STANDARD, PROFESSIONAL, TOPPLAYER,
  SUPERSTAR`.
- Jugador de partida rápida: `pid, matching_order, name/pname, nickname_id, manner, is_owner, side,
  is_leader, is_begin, has_guestplayer, host/reflexive/relayed_addr, ntl_nat_type, ...`.
- `nickname_id` aparece además en la info del jugador, en resultados de partida y en el listado de
  usuarios de ranking por equipos.

## 5. Funciones recientes y su estado

### Fotos de perfil

- El cliente sube con el comando por socket `CMD_SET_AVATAR` (bloque de hasta 12.288 bytes en hexadecimal,
  con su MD5; tabla `Avatars`).
- **Para leer, el cliente nunca usa `CMD_GET_AVATAR` por socket**: manda `CMD_GET_AVATAR_HTTP` con el
  `pid` a la URL de tipo `AVATAR` (`FUN_0058a290` crea el mensaje, `FUN_005af840` le agrega la URL).
- Servidor: `/avatar` (`AvatarHandlers.RenderHttpAsync`) lee el `pid` del cuerpo o de la query y
  responde `result`, `avatar_data_size`, `avatar_md5`, `avatar_data` (o tamaño 0).
- **(sin confirmar)**: cómo viaja exactamente el `pid` y cómo enmarca el cliente el cuerpo de respuesta.
  Se dedujo del decompilado y del patrón de `/ranking`. Mirar la primera petición real en el log HTTP.

### Nicknames

- Identificador `nickname_id` (0 = nickname por defecto del cliente) guardado en `Players.NicknameId` y
  copiado a `PlayerCard.NicknameId` de la sesión al elegir jugador.
- Se establece con `CMD_SET_NICKNAME` o con la clave `nickname_id` del registro `profile` de
  `CMD_SET_PLAYERPROFILE`. `CMD_GET_UNLOCKEDNICKNAMELIST` devuelve `nickname_id_list_num` y
  `nickname_id_list`.
- Los textos se piden por HTTP al tipo `NICKNAME` con `CMD_GET_NICKNAME_COMPE`: `nickname_list_num` y
  `nickname_list[{nickname_id, nickname, nickname_desc}]` (`NicknameHandlers.Catalogue`).
- **(sin confirmar)**: el catálogo real (ids y textos) no está en el exe; los 8 nicknames actuales son
  de relleno y están todos desbloqueados (no hay sistema de logros). Pendiente: desbloqueo por logros y
  verificar el formato de la respuesta.

## 6. Cómo investigar un caso nuevo

1. Reproducir y mirar `C:\Servers\PES2013\logs\server.log` (con `Debug.HexDump`/`LogUnknownCommands`
   activos se ven los comandos y las respuestas que el servidor no conoce).
2. Si el cliente "no pide" algo: casi siempre falta un tipo en `ulist` o una clave en un registro.
3. Buscar la string en `string_xrefs.txt` / el volcado: `CMD_*` para el comando, las claves
   (`nickname_id`, ...) para los registros. Con la función de `xref`, leerla en `all_functions.c`.
4. Las tablas de claves del exe son listas consecutivas de strings; las que rodean a una clave suelen
   ser el resto del registro (ver cómo se hizo con `nickname_id`).
5. Implementar en un handler o en `GameEndpoints.cs`, agregar un test en `tests/OpenEleven.Server.Tests`
   y marcar con `ponytail:` en el comentario lo que quede sin verificar contra un cliente real.
6. Los registros son compartidos con PES 2010-2012: cambiar solo para `GameProfile.Pes2013Pc` si el
   resto de títulos no debe cambiar (`ReferenceParityTests` compara contra el servidor de referencia).

## 7. Operación

- Servidor en producción: `C:\Servers\PES2013` (`OpenEleven.Pes2013Pc.exe`); `conf\server.yaml` y
  `conf\server.local.yaml` son de esa instalación y no se sobrescriben.
- Deploy: `dotnet publish src/OpenEleven.Pes2013Pc -c Release -o <carpeta temporal>`, parar el proceso,
  copiar `OpenEleven.Server.dll` a `.bak`, copiar todo menos `conf`, `data` y `logs`, volver a lanzar el
  exe desde esa carpeta y comprobar que responden el puerto 80 y las rutas HTTP. **El reinicio corta a
  los jugadores conectados**: no desplegar sin que el usuario lo pida.
- La base (SQLite `data\pes2013pc.db`) crea tablas y columnas nuevas sola al arrancar
  (`DatabaseInitializer.EnsureModelSchemaAsync`); agregar una propiedad a una entidad basta.
- `git` instalado en `C:\Program Files\Git\cmd` (si la shell no lo ve, agregarlo al PATH de la sesión).

## 8. Pendientes conocidos

- Confirmar con un cliente real el formato de `/avatar` y `/nickname`.
- Master League online: tipos `ML_*` y sus comandos, sin implementar.
- Desbloqueo de nicknames por logros y catálogo real.
- `TOPMENU`, `WIDGET`, `RSS`, `COMMU_STATS`: tipos conocidos del cliente no anunciados.
- STUN/watchdog de PES 6 detenido (ver memoria del usuario); no relacionado con PES 2013.
