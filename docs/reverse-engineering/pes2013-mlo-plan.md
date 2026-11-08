# PES 2013 PC: plan de Master League Online (MLO)

Plan para implementar Master League Online en el servidor. Sale del volcado de `pes2013.exe`
(strings y `all_functions.c`) y del tráfico real de `C:\Servers\PES2013\logs\server.log`
(2026-11-08). Lo no confirmado contra un cliente real se marca **(sin confirmar)**.

## 1. Qué sabemos

### 1.1 Cómo entra el cliente

Todo MLO va por el socket **Menu** (28043), en estado `PlayerSelected`. Secuencia real del log al
entrar al modo (todo respondió "Unhandled" y el cliente siguió igual):

```
CMD_JOIN_ML_MODE              pid
CMD_GET_ML_MYTEAM             pid                      -> ERR_NOTFOUND = no tiene equipo
CMD_CREATE_ML_MYTEAM          team_name="S.S. LAZIO", team_name_short="LAZ", base_team_id=122, check_team_name=NO
CMD_GET_ML_MYTEAM             pid
CMD_GET_ML_MYTEAM_GAMEPLAYERLIST
CMD_GET_ML_MYTEAM_PLAYER_RATING
CMD_GET_ML_NEWS_LIST          lang=EN, news_type=COMMENT | REGULAR
CMD_LEAVE_ML_MODE             pid
```

Ya existen stubs: `CMD_GET_ML_DIVISIONUPDATE` (`RankHandlers`, siempre `is_update=NO`),
`CMD_SEND_ML_GAMEPLAYERS` (`MatchHandlers.MatchAck`) y las claves `ml_*` vacías del miembro de
partida rápida (`QuickMatchHandlers`, `ml_division=ML_DIVISION_5`, `ml_team_name=""`...).

### 1.2 Comandos (66 en el exe)

Errores posibles sacados de la tabla `comando → ERR_*` del exe (rango ~0x01016xxx del volcado).

| Grupo | Comandos | Errores propios |
|---|---|---|
| Modo | `JOIN_ML_MODE`, `LEAVE_ML_MODE` | |
| Mi equipo | `CREATE_ML_MYTEAM`, `GET_ML_MYTEAM`, `RESET_ML_MYTEAM`, `DELETE_ML_MYTEAM`, `SET_ML_MYTEAM_NAME`, `SET_ML_MYTEAM_BASE`, `SET_ML_MYTEAM_TYPE`, `SET_ML_MYTEAM_UNIFORMNUMBER`, `DECIDE_ML_MYTEAM_PLAYER`, `GET_ML_MYTEAM_HISTORY`, `GET_ML_TEAM_TYPE_REVISION` | `ERR_ALREADYEXISTS`, `ERR_LICENCE`, `ERR_NGWORD`, `ERR_FORBIDDEN`, `ERR_NOTFOUND` |
| Plantel | `GET_ML_MYTEAM_GAMEPLAYERLIST` (+`_OLD`), `GET_ML_MYTEAM_PLAYER_RATING`, `SET_ML_MYTEAM_PLAYER_RATING`, `SEND_ML_GAMEPLAYERS` | |
| Mercado | `GET_ML_TRADE_PLAYER`, `BUY_ML_MARKET_GAMEPLAYER`, `SELL_ML_MARKET_GAMEPLAYER`, `EXTEND_ML_MARKET_GAMEPLAYER`, `GET/ADD/DELETE_ML_MARKET_CHECKLIST`, `GET_ML_MARKET_ENV`, `GET_ML_TRADEHISTORY`, `SEARCH_ML`, `SEARCH_ML_ALL/LEAGUE/NATIONAL`, `REQWATCH_ML_MARKET_PRICE`, `ACK_ML_MARKET_PRICE` | `ERR_INSUFFICIENT_FUNDS`, `ERR_TRADE_STOP`, `ERR_TEAM_MEMBER_FULL`, `ERR_TEAM_MEMBER_LACK`, `ERR_ENABLE_BUY_NUM`, `ERR_COMPE_ENTRY`, `ERR_OVEREXTEND` |
| Economía de partido | `GET_ML_MATCHINCOME_INFO`, `GET_ML_MATCHEXPENSE`, `GET_ML_MATCHRECORD`, `GET_ML_GAMERESULTS` | |
| Rankings / divisiones | `GET_ML_RANKING`, `GET_ML_RANKING_BYID`, `GET_ML_MYRANKING`, `GET_ML_DIVISION`, `GET_ML_DIVISIONUPDATE`, `GET_ML_DIVISIONRANKING`(+`_BYID`) | |
| Rival Rank | `GET_ML_RIVAL_RANK`, `_LAST`, `_STATUS`, `_BONUS` | `ERR_NO_WIN_POINT` |
| Competiciones | `GET_ML_COMPE_PRIZE`, `GET_ML_COMPE_GAMEPLAYERLIST`, `ADD_ML_EVENT_COMPE_TEST` (solo pruebas) | |
| Noticias / eventos | `GET_ML_NEWS_LIST`, `SET_ML_NEWS_COMMENT` y `_TEAM_CREATE/_TEAM_RESET/_BUY/_SELL/_COMPE_CHAMP/_RIVAL_RANK_CHAMP`, `GET_ML_EVENTLIST`, `GET_ML_MYTEAM_EVENTLIST`, `GET_ML_REGULAREVENTLIST` | `ERR_NGWORD` |
| Archivo de parámetros | `GET_ML_PLAYERPARAMSIZE` | `ERR_DATANOTFOUND` |

### 1.3 Comandos que van por HTTP

Tabla `comando → urltype` del exe (0x0141f07c). Hay que anunciarlos en `GetUrlList` y servir la ruta:

| urltype | Comandos |
|---|---|
| `ML_RANKING` | `CMD_GET_ML_RANKING` |
| `ML_RANKING_BYID` | `CMD_GET_ML_RANKING_BYID` |
| `ML_DIVISIONRANKING` / `ML_DIVISIONRANKING_BYID` | `CMD_GET_ML_DIVISIONRANKING` / `_BYID` |
| `ML_RIVAL_RANK` | `CMD_GET_ML_RIVAL_RANK`, `_LAST`, `_STATUS`, `_BONUS` |
| `ML_PRICERANKING`, `ML_GAINRANKING`, `ML_POPULARRANKING` | `CMD_GET_ML_MARKET_PRICERANKING`, `_GAINRANKING`, `_POPULARRANKING` |
| `ML_GAMEPLAYERHISTORY` | `CMD_GET_ML_MARKET_GAMEPLAYERHISTORY` |
| `ML_MARKET_PLAYER_PARAM_SIZE` | `CMD_GET_ML_PLAYERPARAMSIZE` (`file_size`, `uncompressed_file_size`) |
| `ML_MARKET_PLAYER_PARAM` (tipo 0x1f) | el archivo: una línea de texto por futbolista, base de la búsqueda del mercado |
| `TOPMENU` | `CMD_GET_TOPMENU_INFO` (incluye `ml_popular_rank_list`, `mlo_compe_notice`, `mlo_last_compe`) |

### 1.4 Claves de los registros (tablas de claves del exe)

- **Equipo** (`GET_ML_MYTEAM` → `myteam`): `team_funds, team_name, team_name_short, ml_skill_level,
  base_team_id, enable_buy_num, point, team_power, total_price_old, team_resource, team_status`
  (`team_status` conocido: `CREATED`).
- **Jugador del plantel** (`player_list`): `gameplayer_id, uniform_number, tiredness, ml_condition`
  (`EXCELENT|GOOD|NORMAL|POOR|TERRIBLE`), `injury{part, symptom, level, level_no_self_conscious,
  mcount_left}`, `contract_term, contract_price_list, now_yellow_card_count, now_red_card_count,
  owner_num, games`.
- **Plantel con precios** (`GET_ML_MYTEAM_GAMEPLAYERLIST`): `gameplayer_id, market_price,
  market_price_old, buy_price, market_price_max/min, uniform_number, overall, goal, assist, games,
  is_trade_stop, trade_stop_date, trade_stop_term, injury{...}, ml_condition, now_*_card_count,
  contract_term, owner_num`.
- **Valoraciones**: `active_user_num, rating_num, rating_list[gameplayer_id, rating]`; al enviar:
  `player_list_num, player_list[gameplayer_id, position, play_time], rating_list`.
- **Mercado** (`GET_ML_TRADE_PLAYER` → `trade_player`): `market_price, contract_price_list,
  base_price, contract_price_base, owner_num, active_team_num, games, rating_avg`. Lista de búsqueda:
  `gameplayer_list[gameplayer_id, market_price, market_price_old/max/min, overall, is_trade_stop,
  trade_stop_date, trade_stop_term, contract_price, owner_num, ml_condition], total_gameplayer_num`.
  Respuestas: compra → `market_price, contract_price, enable_buy_num, team_funds`; extensión →
  `contract_price, team_funds`; contratos `CONTRACT_TYPE1..3` con `extend_contract_term`.
- **Historial de precio** (HTTP): `owner_num, history_num, price_history[market_price,
  market_price_max, market_price_min]`. Rankings de mercado: `rank_list[gameplayer_id, market_price,
  owner_num, market_price_this_week, market_price_last_week], date`.
- **Ranking ML** (HTTP): pide `start_index, order=ASC, rankall_kind=DIVISION_POINT|WIN|MATCH|TEAM_FUNDS|TEAM_POWER`;
  responde `date, total_rank_num, rank_list[pid, rank, base_team_id, team_resource, team_power,
  ml_rating, match_rec{win_num, draw_num, lose_num}, con_win, name, team_name]`.
- **Mi ranking**: `teampower_rank, teampower_total_rank_num, teamfunds_rank, teamfunds_total_rank_num,
  rating_rank, rating_total_rank_num, date`. **Récord**: `match_record{win_num, lose_num, draw_num}`.
- **Economía de partido**: `expense`; `income, prize_income, yellowcard_count, redcard_count,
  purchase_count, team_funds, fb_bonus, derby_income`.
- **Rival Rank**: estado `is_first_login, is_rank_entry, has_rank_last, status=ENTRY|PENALTY|INACTIVE|BEGINNER|LESS_RIVAL`;
  ranking `rival_player_num, price_list, limit_time, rank_list[pid, rank, match_record, name, nick_name_id]`;
  bonus `income, post_team_funds`.
- **Competición**: `prize, team_funds, compe_result, compe_name, my_pre_rank`; las competiciones ML
  reusan COMPLG con `is_ml_compe` y `entry_limit_team_power_min/max`.
- **Noticias**: `news_list[news_type, news_kind, text, comment, pid, name]`. **Eventos**:
  `event_list[event_date, event_type=TEAM_CREATE|TEAM_NAME|TEAM_RESET|BUY|SELL|COMPE|RIVAL_RANK_CHAMP]`.
- **Partido**: el miembro de partida rápida ya lleva `ml_rating, ml_team_name, ml_base_team_id,
  team_power, ml_team_type, derby_income`; `CMD_SET_QUICKMATCHOPTION` trae `is_team_power` y
  `ml_is_division`. El partido MLO es una partida rápida con datos ML (**sin confirmar** el modo
  exacto: aparecen las cadenas `ML_RANK` y `ML_COMPE`).

## 2. El catálogo de jugadores (resuelto en la fase 0)

El servidor maneja `gameplayer_id` (id de jugador de la base del juego), precios y salarios. La base
está en la instalación (`C:\Program Files (x86)\KONAMI\Pro Evolution Soccer 2013`):

- **Formato**: `img\dt*.img` son archivos AFS (`"AFS\0"`, `count` en +4, tabla `offset,size` desde +8).
  Cada entrada suele estar comprimida: cabecera de 16 bytes `00 01 01 "WESYS"`, tamaño comprimido en
  +8, descomprimido en +12, y después un stream zlib. Kitserver (`kitserver13\config.txt`, `afs2fs`)
  reemplaza entradas con los archivos `kitserver13\<dir>\img\dtXX.img\unnamed_N.bin`, en el orden
  `pesedit`, `pesedit-ml`, `pesedit-stadiums`, `pesedit-scoreboards`, `pesedit-ol`. El extractor
  tiene que aplicar esos reemplazos.
- **Jugadores**: `dt0f.img` entrada **1975** (PESEDIT, 2.540.636 bytes descomprimida). Cabecera de
  28 bytes y después **11.143 registros de 228 bytes**: `+0` u32 (?), `+4` u32 **id**, `+8` nombre
  japonés (46 bytes UTF-8), `+54` **nombre** (46 bytes UTF-8), `+100` nombre de camiseta (16 bytes) y
  el resto son atributos empaquetados en bits. Comprobado: Messi = 7511, Xavi = 866, Iniesta = 4073.
- **Planteles**: `dt0f.img` entrada **1980**. Cabecera de 32 bytes y **470 equipos de 164 bytes**:
  `+0` u32 id de equipo, `+4` 32 × u32 id de jugador (0 = vacío), `+132` 32 bytes de dorsal (`FF` =
  vacío). Comprobado: 108 Barcelona (Valdés, Puyol, Piqué, Xavi, Messi…), 122 Lazio (el
  `base_team_id=122` que eligió el jugador del log).
- **Plantel inicial de ML**: equipo **140 "MASTERLEAGUE DEFAULT"**, 28 jugadores (ids 55244-55271:
  Ivyghasek, Stramberg, Minandinho, Castolis…). También existen 141/142 (HIDE1/2), 1416 (DEFAULT J),
  2469 (DEFAULT L) y los juveniles 2511-2513 (ML NEWFACE EURO/LATINAMERICA/JAPAN).
- **Nombres de equipo**: `dt0f.img` entradas 1979/1981 (multilenguaje), y `kitserver13\teamlist.txt`
  (id → nombre) para cruzar.
- `pesedit-ml` reemplaza `dt0f` 1982 (configuración de ML, 23 secciones, sin decodificar) y 1991.
- **Pendiente**: decodificar los atributos empaquetados (bytes +116 a +227) para sacar el overall, la
  posición, la edad y la nacionalidad. Se calibra contra jugadores conocidos (Messi, Xavi). Si se
  complica, el overall puede salir de un CSV de la comunidad cruzado por id.

Plan: una herramienta `tools/OpenEleven.MlCatalog` lee la instalación y escribe
`data/ml-catalog.json` (jugadores y planteles). El servidor lo carga al arrancar. El prototipo
(lector AFS/WESYS y volcado de planteles) quedó probado en C#.

## 3. Fases

Cada fase termina con tests en `tests/OpenEleven.Server.Tests`, actualización de este documento y de
`pes2013-online-structure.md`, y un deploy de prueba.

### Fase 0 — Investigación (hecha, 2026-11-08)

1. **Parsers de respuesta.** `GET_ML_MYTEAM` lo lee `FUN_005ca950(…, "myteam")`: las claves van
   dentro de un registro `myteam` y **son todas opcionales**. Si falta una, el cliente pone 0 o vacío;
   si falta `base_team_id`, pone 0xFFFF. `team_status="CREATED"` es lo único que marca el equipo como
   creado. `team_name`, `team_name_short` y `ml_skill_level` son cadenas; `team_funds`,
   `enable_buy_num`, `point`, `team_power`, `total_price_old` y `team_resource` son enteros. El
   plantel (`player_list_num` / `player_list[]`, parser cerca de `all_functions.c:391600`) lee
   `injury` como subregistro (`part, symptom, level, level_no_self_conscious, mcount_left`) y
   `contract_price_list` como lista de enteros (uno por `CONTRACT_TYPE1..3`). `ml_condition` usa
   `EXCELENT|GOOD|NORMAL|POOR|TERRIBLE` (con esa ortografía).
2. **Entrada al modo.** `CMD_JOIN_ML_MODE` (`FUN_005c9a90`) y `CMD_LEAVE_ML_MODE` solo miran si hubo
   error: con `NOERR` alcanza. En `GET_ML_MYTEAM`, `ERR_NOTFOUND` **no** es un error: significa "no
   tiene equipo" y lleva a la pantalla de creación (confirmado en el log: después vino
   `CREATE_ML_MYTEAM`). Cualquier otro error pone el modo en estado de error (`+0xe00`).
3. **Catálogo**: resuelto, ver sección 2. Falta decodificar el overall.
4. **Partido MLO.** Es una **partida rápida** con `type="ML_RANK"` (MLO normal) o `"ML_COMPE"`
   (competición). El cliente acepta como tipo `OC_RANK`, `OC_FREE`, `COMPE`, `ML_RANK` y `ML_COMPE`
   (`all_functions.c:2296617`); hoy `QuickMatchHandlers` solo distingue `OC_Rank` y lo demás.
   El game plan también tiene modo: `CMD_GET/SET_GAMEPLAN` llevan `mode="ML"|"ML_COMPE"|"COMPE"`
   (`FUN_005d2740`/`FUN_005d27c0`), y el servidor guarda **un solo** game plan por jugador, así que
   hoy el de MLO pisa al normal. Al terminar, el reportador del partido (el mismo que manda
   `CMD_SET_COMMU_MATCHINFO`, cerca de `all_functions.c:485151`) envía, en este orden:
   `CMD_SEND_ML_GAMEPLAYERS` (`gameplayer_id_list_num`, `gameplayer_id_list`: los que jugaron),
   `CMD_SET_ML_MYTEAM_PLAYER_RATING` (`rating_num`, `rating_list[gameplayer_id, rating]`, y además
   `player_list[gameplayer_id, position, play_time]`). Ingresos y gastos los calcula el servidor; el
   cliente los pide después con `GET_ML_MATCHINCOME_INFO` / `GET_ML_MATCHEXPENSE`. El plantel del rival
   viaja por P2P (`is_team_data_load` en `roomplayer_list`) **(sin confirmar)**.
5. **`ML_MARKET_PLAYER_PARAM`.** `CMD_GET_ML_PLAYERPARAMSIZE` (`FUN_0065f740`) devuelve `file_size` y
   `uncompressed_file_size`. Con `file_size=0` el cliente se salta la descarga (`FUN_0065f520`),
   pero **el archivo no es opcional**: la búsqueda del mercado recorre sus líneas
   (`FUN_0065fa20`, `FUN_00662ab0`), así que sin él el mercado sale vacío (lo vimos en la primera
   prueba real). El archivo se baja de la URL tipo 0x1f (`ML_MARKET_PLAYER_PARAM`). Es texto, una
   línea por futbolista con 7 valores separados por espacios: `gameplayer_id market_price
   fin_bloqueo condición is_trade_stop owner_num contract_price`. Se ignora la línea con id 0 o
   con el último valor en -1; `is_trade_stop≠0` lo oculta. Si los dos tamaños coinciden, va sin
   comprimir (si no, se descomprime con `FUN_01302ac0`). El significado de los valores 3 a 5 sale
   de a qué campos los copia el cliente, no de una captura; el servidor manda condición 240
   (`NORMAL`) y ningún bloqueo.

### Fase 1 — Modo y equipo propio

- Herramienta `tools/OpenEleven.MlCatalog` y `data/ml-catalog.json` (sección 2). El plantel inicial
  es el del equipo 140; el `base_team_id` elegido solo da nombre, escudo y uniforme.
- Entidades: `MlTeam` (pid, nombre, corto, base_team_id, fondos, puntos, división, tipo, estado,
  enable_buy_num, creado), `MlTeamPlayer` (equipo, gameplayer_id, dorsal, contrato restante,
  salario, cansancio, condición, lesión, tarjetas, partidos, goles, asistencias, precio de compra),
  `MlEvent`.
- `MlHandlers`: `JOIN/LEAVE_ML_MODE`, `CREATE/GET/RESET/DELETE_ML_MYTEAM`, `SET_ML_MYTEAM_NAME/BASE/TYPE/UNIFORMNUMBER`,
  `GET_ML_MYTEAM_GAMEPLAYERLIST`, `GET/SET_ML_MYTEAM_PLAYER_RATING`, `GET_ML_EVENTLIST`,
  `GET_ML_NEWS_LIST` vacío, `GET_ML_MYTEAM_HISTORY`.
- Configuración `Server:MasterLeague` (fondos iniciales, plantel inicial, tope de plantel 23-30,
  compras por semana) en `conf/server.pes2013pc.yaml`.
- Resultado: crear equipo, ver plantel y volver a entrar con el equipo guardado.

### Fase 2 — Mercado de fichajes

- `MlMarket`: precio de mercado por jugador = base (overall) × demanda (`owner_num`), recalculado
  semanalmente con historial (`MlPriceHistory`), máximo/mínimo, `trade_stop` para jugadores bloqueados.
- Comandos: `GET_ML_TRADE_PLAYER`, `SEARCH_ML*`, `BUY/SELL/EXTEND_ML_MARKET_GAMEPLAYER` (contratos
  1-3 temporadas, fondos, tope de plantel, compras por periodo, bloqueo durante competición),
  checklist, `GET_ML_MARKET_ENV`, `GET_ML_TRADEHISTORY`, `REQWATCH/ACK_ML_MARKET_PRICE`.
- HTTP: `ML_GAMEPLAYERHISTORY`, `ML_PRICERANKING`, `ML_GAINRANKING`, `ML_POPULARRANKING`,
  `ML_MARKET_PLAYER_PARAM_SIZE`.
- Varios jugadores pueden tener al mismo futbolista (`owner_num`): el mercado no es exclusivo.

### Fase 3 — Partidos MLO y economía

- Partida rápida `type=ML_RANK`/`ML_COMPE`: emparejar solo jugadores del mismo tipo y rellenar las
  claves `ml_*` y `team_power` del miembro con el equipo real.
- Game plan por modo (`mode=ML|ML_COMPE|COMPE`, además del normal) en vez de uno solo por jugador.
- Al cerrar el partido: ingresos (`income`, `prize_income`, `derby_income`, `fb_bonus`), gastos
  (salarios por partido, tarjetas), `GET_ML_MATCHINCOME_INFO`/`GET_ML_MATCHEXPENSE`, contratos que
  bajan por partido y vencen, cansancio, condición, lesiones y suspensiones.
- `SEND_ML_GAMEPLAYERS` y `SET_ML_MYTEAM_PLAYER_RATING`: alineación, minutos y notas.
- `GET_ML_MATCHRECORD`, `GET_ML_GAMERESULTS`, `ml_rating` y puntos de división.

### Fase 4 — Rankings y divisiones

- `GET_ML_RANKING`(+`BYID`) por HTTP con los cinco `rankall_kind`, `GET_ML_MYRANKING`,
  `GET_ML_DIVISION`, `GET_ML_DIVISIONRANKING`(+`BYID`), `GET_ML_DIVISIONUPDATE` real (ascensos y
  descensos por temporada, `SE_ML_UP/DWN`).
- Página en el sitio web con el ranking MLO.

### Fase 5 — Competiciones ML y Rival Rank

- Competiciones COMPLG con `is_ml_compe=YES`, límite por `team_power`, premios
  (`GET_ML_COMPE_PRIZE`), `GET_ML_COMPE_GAMEPLAYERLIST`, bloqueo de traspasos (`ERR_COMPE_ENTRY`).
- Rival Rank por HTTP: estado, ranking actual y anterior, bonus semanal.

### Fase 6 — Noticias, eventos y top menu

- `SET_ML_NEWS_COMMENT_*` (con filtro de palabras), `GET_ML_NEWS_LIST` COMMENT/REGULAR, eventos
  regulares, `TOPMENU` con jugadores populares y avisos de competiciones MLO.

## 4. Decisiones abiertas

1. **Catálogo**: decidido, se extrae de `dt0f.img` con los reemplazos de PESEDIT. Queda abierto de
   dónde sale el overall: decodificar los atributos (recomendado) o un CSV externo.
2. **Economía**: valores iniciales (fondos, salarios, premios). Sin datos del servidor original; se
   propone dejarlos configurables y ajustar con partidos reales.
3. **Alcance de la primera entrega**: fases 0-2 hacen jugable el menú (equipo + mercado); los partidos
   (fase 3) son lo que más riesgo tiene.

## 5. Estado (2026-11-08) y puntos grises

Fases 0 a 6 implementadas, con tests (`tests/OpenEleven.Server.Tests/MasterLeague*Tests.cs`). Nada
se probó todavía con un cliente real ni se desplegó. El overall sale de la media de los diez mejores
atributos (Messi 95, Xavi 91). La economía es configurable en `Server:MasterLeague`.

Lo que queda por definir o confirmar:

1. **Partida rápida `_NTL` sin probar.** El flujo se portó del de 2011 con las claves del exe. Falta
   confirmar que el cliente manda `CMD_SEND_IPANDPORT_NTL` antes de buscar desde el menú; sin eso, el
   aviso sale con direcciones vacías.
2. **Formato HTTP de MLO.** No está confirmado cómo arma el cliente la petición ni si espera el cuerpo
   tal cual (como `/nickname`) o envuelto. Hay que mirar el primer pedido real en el log HTTP.
3. **Reglas económicas inventadas.** Precios (×1,25 por punto de overall, +2 % por dueño), salarios
   (0,5 % por partido), ingresos por resultado, contratos de 10/20/30 partidos, tope de 60, plantel de
   18 a 32 y 5 compras por semana.
4. **Overall aproximado.** No se aplica la ponderación por posición del juego, y no se decodificaron
   posición, edad ni nacionalidad.
5. **Valores de enumerados desconocidos.** `ml_skill_level` (se usa el enum de lobby según la
   división), `team_resource` (se manda el valor del plantel), `ml_team_type` y la clave de
   `CMD_SET_ML_MYTEAM_TYPE`, `compe_result` (se manda el puesto) y las partes y síntomas de las
   lesiones (siempre 1).
6. **Reglas de partido simplificadas.** Las lesiones duran 2 partidos fijos; la roja solo se cuenta,
   sin suspensión. Un contrato vencido se va solo si el plantel no queda por debajo del mínimo. Los
   fondos pueden quedar negativos.
7. **Rival Rank.** Las reglas son propias: grupo = división actual, semana lunes-domingo UTC, `BEGINNER`
   con menos de 5 partidos y `PENALTY` nunca se usa. Se desconoce la clave de la petición de
   `ML_DIVISIONRANKING`; se lee `division`.
8. **Sin implementar** (el cliente no los envía o no se sabe qué esperan): `TOPMENU` (no anunciado),
   `CMD_REQWATCH_ML_MARKET_PRICE` (solo se acepta, no se empuja nada), `GET_ML_REGULAREVENTLIST` (vacío),
   `GET_ML_DIVISIONUPDATE` (sigue `is_update=NO`), y los comandos que solo aparecen en la tabla de
   errores (`SEARCH_ML*`, `GET_ML_MARKET_ENV`, `GET_ML_TRADEHISTORY`, `GET_ML_DIVISION`,
   `GET_ML_GAMERESULTS`, `DECIDE_ML_MYTEAM_PLAYER`, `GET_ML_TEAM_TYPE_REVISION`, `GET_ML_MYTEAM_HISTORY`,
   `GET_ML_COMPE_GAMEPLAYERLIST`).
9. **Página web.** No se agregó un ranking MLO al sitio.
