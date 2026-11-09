# PES 2013 PC: plan de Online Competition (COMPLG)

Plan para completar las competiciones online (copas tipo KONAMI CHAMPIONSHIP) en el servidor. Sale del
volcado de `pes2013.exe` (tablas de enumerados, `all_functions.c`) y del tráfico real de
`C:\Servers\PES2013\logs\server.log` (2026-11-08). Lo que no se confirmó contra un cliente real se
marca **(sin confirmar)**.

## 1. Qué sabemos

### 1.1 Estado del servidor

`Handlers/CompetitionHandlers.cs` viene de PES 2011 y cubre la consulta, la inscripción y la fase de
clasificación: `GET_COMPLG_KIND_LIST`, `_LIST`, `_INFO`, `_MYENTRY_COMPEID`, `JOIN/LEAVE/QUIT_COMPLG`,
`SET_COMPLG_TEAM`, `GET_COMPLG_TEAM_DATA`, `_TEAMRANKING`, `_PRE_RESULTS`, `_GOALRANKING`,
`_ASSISTRANKING`, `_MYRESULTS`, `_MATCH_START_TIME`, `REQWATCH_COMPLG_PLAYERNUM` y un `_TITLEHOLDER` vacío.
Las competiciones se definen en `Server:Competitions` (YAML). Un partido de clasificación es una partida
rápida `type=COMPE`/`ML_COMPE` cuyo `Room.CompeId` pasa a `MatchRecord.CompeId`.

Los comentarios del handler citan direcciones de **PES 2011** (`FUN_0055cdb0`...). En 2013 los parsers
son otros y piden otras claves (sección 1.3).

### 1.2 Lo que hace hoy el cliente 2013 (log)

```
CMD_JOIN_COMPLG_MODE   jdlc_type=NOTHING          -> Unhandled
CMD_GET_COMPLG_LIST    compe_list_kind=NOW_PLAY, compe_kind=KONAMI_CHAMPIONSHIP, compe_mode=NORMAL
                       -> compe_num=0 (no hay competiciones en la config)
CMD_LEAVE_COMPLG_MODE                             -> Unhandled
```

### 1.3 Enumerados de 2013 (tabla en 0x016908f8, cantidades en 0x01501a58)

Decodificados del volcado; difieren de los de 2011 y el cliente descarta el registro entero si un nombre
no está en la tabla:

| Enum | Valores | Diferencia con hoy |
|---|---|---|
| 0x0d `category` | PLATINUM GOLD SILVER BRONZE | igual |
| 0x0e `entry_limit` | SKILLLEVEL FACEBOOK TEAMPOWER J_LEAGUE J_LEAGUE_OPEN | **el servidor manda `DIVISION`, que no existe en 2013** |
| 0x0f / 0x17 `compe_kind` | UEFA_Champions_League, Copa_Santander_Libertadores(_2013), International_Club_Championship, International_Cup_Online, KONAMI_CHAMPIONSHIP, Facebook_Cup, J_League_Cup, WEPES_CLUB_OPEN, WEPES_NATIONAL_OPEN, WEPES_CHAMPIONSHIP, Novice_Cup, ML_UEFA_Champions_League, ML_Open(_Coach_Mode), ML_High/Middle/Normal_Class_Cup(_Coach_Mode), ML_Novice_Cup(_Coach_Mode), ML_Facebook_Cup, RESERVED_CUP01..100 | **el valor por defecto `KONAMI_CUP_ONLINE` no existe en 2013** |
| 0x10 `compe_list_kind` | NOW_PLAY NOW_WATCH OLD_ALL OLD_MY SCHEDULE | igual |
| 0x11 | MATCH_1..MATCH_4 | (sin confirmar dónde se usa) |
| 0x12 `compe_mode` | NORMAL ML | nuevo: separa copas normales y de MLO |
| 0x13 `status` | NOCREATE CREATED PERMITSTART ENTRY STARTED ENTRY_CLOSED MATCH_CLOSED FINISHED CANSELED END CLEARED DELETED | igual |
| 0x14 `end_kind` (`WATCH_COMPLG_END`) | SECOND_ROUND_EXIT FINAL_ADVANCEMENT END_PLAYOFF | nuevo |
| 0x15 `gametime` | TIME_5 TIME_10 TIME_15 | igual |
| 0x16 `compe_result` | VICTORY SECOND_PLACE BEST_4 BEST_8 FINAL_ADVANCEMENT SECOND_ROUND_EXIT NON | **`GET_ML_COMPE_PRIZE` manda un número** |
| 0x18 fin de partido | NORMAL_END BEFORE_ERR GAMEING_ERR TIMEOUT DEGRADE | nuevo (`WATCH_COMPLG_END_MATCH`) |
| 0x2e tipo de correo | MAIL_NORMAL, CUP_WR_FINALWIN, CUP_WR_FINALPRIZE, CUP_WR_NORMALWIN, CUP_WR_NORMALPRIZE, CUP_WR_PREPASS, CUP_WR_GOAL, CUP_WR_ASSIST, CUP_NOREPLAY, CUP_NORMALEND, CUP_MUKOUEND, CUP_MLONLY... | avisos de la copa por correo |
| 0x32 modo de partido | RANK_MATCH ML_RANK_MATCH COMPE_MATCH ML_COMPE_MATCH COMMU_1VS1_FREE COMMU_MATCH | el historial manda siempre `RANK_MATCH` |
| 0x52 partida rápida | OC_RANK COMPE ML_RANK ML_COMPE | igual |

### 1.4 Claves de los parsers 2013

Sacadas de las strings de cada función (las claves comunes como `pid` o `name` van por `DAT_` y hay que
leerlas en el decompilado al implementar):

- **Lista** (`FUN_005b31e0`): `compe_kind, compe_id, entry_start/end_time, match_start/end_time,
  compe_end_time, entry_limit_team_power_min/max, compe_name, status, category, entry_limit,
  entry_limit_facebook, entry_limit_skill_level, dlcver, player_capacity, player_num, champion_notice,
  is_coach_mode, need_konamiid, banner_file_name`. Ya no hay `entry_limit_rank` ni `entry_limit_division`.
- **Detalle** (`FUN_005b3fc0`, dentro de `compe_data`): lo de la lista más `use_national, use_club,
  use_ml, useable_team_size, useable_team_id, usable_ball_pre/playoff/final_num` y sus `_list`,
  `gametime`, **`compe_fee`** (hoy se manda `fee`), `comment`, `get_ball_id`, `get_spike_id`,
  `prize_second_round_list`, `prize_tournament_list`, `ad_file_name`.
- **Inscripción** (`FUN_005b5650`): la petición trae `is_entry`; la respuesta, `is_select_team`.
- **Tipos** (`FUN_005bc560`): la petición trae `compe_mode`.
- **Campeón** (`FUN_005ba010`/`FUN_005ba080`): la petición trae `is_ml_compe`; la respuesta, `start_time,
  team_id, compe_name, trophy, banner_file_name, cup_file_name`.
- **Felicitación** (`GET_COMPLG_COMMENDATION_DATA`, `FUN_005b8230`): `compe_name, trophy, team_id,
  cup_file_name`. Los nombres de trofeo y banner son recursos del cliente (`compeTrophyKONAMIChamp`,
  `compeBannerMLOpen`...).
- **Plantel** (`FUN_005ba990`): suma `injury_all, injury_one, injury_data, tiredness` a lo que ya se guarda.

### 1.5 La fase final (playoff), que no existe en el servidor

Comandos con constructor en el cliente (los que solo aparecen en la tabla de errores no se mandan):

| Comando | Dirección | Claves |
|---|---|---|
| `CMD_WATCH_COMPLG_MATCH_START_TIME` | push | hora de la ronda (luego el cliente pide `GET_COMPLG_MATCH_START_TIME`) |
| `CMD_WATCH_COMPLG_MATCH_START` | push | `is_match_start` |
| `CMD_GET_COMPLG_PLAYOFF_PLAY_OK` | petición | `has_guestplayer` |
| `CMD_GET_COMPLG_PLAYOFF_MATCH_NTL` | petición | respuesta con el rival: `team_id, team_name, host_addr, reflexive_addr, relayed_addr, ntl_nat_type, ml_team_type, xsession_info` (`FUN_005bc050`) |
| `CMD_WATCH_SERVERMATCHEND` | push | `limit_time` |
| `CMD_WATCH_COMPLG_END_MATCH` | push | `end_kind, is_pk, is_draw, win_pid` |
| `CMD_WATCH_COMPLG_END` | push | `end_kind` (0x14), `is_disqualified` |
| `CMD_WATCH_COMPLG_ABORT` | push | (vacío) |
| `CMD_GET_COMPLG_TOURNAMENT` | petición `compe_id` | cuadro: participantes (`team_id, is_restricted, rating, skill_level, last_result, nickname_id`) y cruces (`is_end, home_pid, away_pid, win_pid, home_score, away_score`) (`FUN_005bb1a0`, `_5bb6b0`, `_5bbc70`) **(sin confirmar qué parser es de cuál comando)** |
| `CMD_GET_COMPLG_PLAYOFF_RESULTS` | petición `compe_id` | resultados del playoff |
| `CMD_REQWATCHL_COMPLG_MATCHLIST` | lista observada | `match_list_num`, `match_list`, id `match_id`, máximo 8 (partidos en curso, `NOW_WATCH`) |

Sin constructor en el cliente (no hace falta): `CREATE_COMPLG`, `ADD_COMPLG_RESULTS`,
`GET_COMPLG_DIGESTLIST`, `GET_COMPLG_PLAYOFF_MATCH` (sin `_NTL`), `WATCH_COMPLG_GAMEDATA`,
`WATCH_COMPLG_MATCHNOTICE`.

Flujo deducido: clasificación (liga con partidas rápidas `COMPE`) → al cierre los mejores pasan
(`is_qualifier`) → el servidor avisa la hora de cada ronda → a la hora, `MATCH_START` → el cliente pide
`PLAYOFF_PLAY_OK` y `PLAYOFF_MATCH_NTL` → partido P2P → `END_MATCH` a los dos → el perdedor recibe
`END(SECOND_ROUND_EXIT)` y el ganador sigue; al final `END(END_PLAYOFF)`, felicitación y campeón.
**(sin confirmar)** el orden exacto: se sacará de la máquina de estados (`param_1 + 0x708`, `+0x4ab4`).

## 2. Fases

Cada fase termina con tests en `tests/OpenEleven.Server.Tests`, actualización de este documento y de
`pes2013-online-structure.md`, y deploy.

### Fase 0 — Compatibilidad 2013 (bloqueante, chica)

- `CMD_JOIN_COMPLG_MODE` / `CMD_LEAVE_COMPLG_MODE` con `NOERR` (confirmar en el parser si la respuesta
  necesita algo más).
- `compe_kind` por título: valor por defecto `KONAMI_CHAMPIONSHIP` en 2013 y validación de la config
  contra la tabla 0x0f al arrancar (avisar en el log si un nombre no existe).
- `compe_mode`: `NORMAL` lista solo copas normales, `ML` solo las de MLO (`KIND_LIST` y `LIST`).
- Claves 2013 de lista y detalle (1.4): `entry_limit` en `SKILLLEVEL`/`TEAMPOWER`, `entry_limit_skill_level`,
  `entry_limit_facebook`, `compe_fee`, `comment`, `banner_file_name`, listas de premios y balones.
  Rehacer los comentarios con las funciones de 2013.
- `GET_ML_COMPE_PRIZE.compe_result` con el enum 0x16.
- Competición de ejemplo en `conf/server.pes2013pc.yaml`.
- Resultado: la copa aparece en la lista, abre el detalle y uno se inscribe y elige equipo.

### Fase 1 — Clasificación

- Partida rápida `COMPE`/`ML_COMPE`: emparejar solo inscritos en la misma copa, con equipo elegido y con
  la copa en `STARTED`; si no, los errores del cliente (`ERR_COMPE_NOTENTRY`, `ERR_COMPE_STATUS`,
  `ERR_COMPE_NOT_SELECT_TEAM`).
- Historial con `COMPE_MATCH`/`ML_COMPE_MATCH`; decidir si estos partidos mueven rating y puntos.
- Empujar `player_num` cuando alguien entra o sale; `REQWATCHL_COMPLG_MATCHLIST` con los partidos en curso.
- `is_qualifier` en la tabla al cerrar la clasificación; `OLD_MY` desde `CompetitionEntries` (hoy usa
  `Players.CompetitionId`, que se pierde al entrar en otra copa).

### Fase 2 — Playoff (la parte grande)

- Entidad `CompetitionBracket` (copa, ronda, cruce, local, visitante, goles, penales, ganador, estado).
- Servicio de calendario (`BackgroundService`) que avanza las copas según sus horas: cierra la
  clasificación, siembra el cuadro con los N primeros (potencia de 2), avisa las rondas y arranca cada
  una.
- `PLAYOFF_PLAY_OK`, `PLAYOFF_MATCH_NTL` (sala de dos con las direcciones NTL, como la partida rápida),
  resultado → `END_MATCH` → avance o eliminación (`END`). Sin empate: prórroga/penales (`is_pk`).
- Incomparecencias: si un rival no aparece en `limit_time`, pasa el otro (`WATCH_SERVERMATCHEND`).
  Desconexión: la regla de abandono que ya existe.
- `GET_COMPLG_TOURNAMENT`, `GET_COMPLG_PLAYOFF_RESULTS`; `WATCH_COMPLG_ABORT` si se cancela la copa.
- `Config`: tamaño del cuadro, hora de cada ronda (o margen entre rondas), tiempo de espera.

### Fase 3 — Cierre, premios y campeón

- `GET_COMPLG_TITLEHOLDER` real (por `is_ml_compe`), `COMMENDATION_DATA`, `champion_notice`, trofeo y
  banner por `compe_kind` (mapa configurable a los recursos `compeTrophy*` / `compeBanner*`).
- Correos `CUP_WR_*` (pasó la clasificación, campeón, goleador, asistidor) por `MailHandlers`.
- MLO: premio por puesto final con playoff (`prize_tournament_list`), noticia `COMPE_CHAMP`, bloqueo de
  traspasos (ya existe).

### Fase 4 — Operación

- Copas recurrentes generadas solas (por ejemplo, una por semana) en vez de fechas fijas en el YAML.
- Página web con el cuadro y los resultados; alta/edición desde el panel de administración (opcional).
- `TOPMENU` (`compe_notice`, `last_compe`, `mlo_compe_notice`), pendiente compartido con MLO.

## 3. Decisiones abiertas

1. **Formato**: clasificación por liga + playoff de N (como Konami), o solo liga. Tamaño del cuadro.
2. **Horarios del playoff**: rondas a hora fija para todos (lo que espera el cliente con
   `MATCH_START_TIME`) o jugar apenas estén los dos.
3. **¿Los partidos de copa mueven rating/división?**
4. **Definición de las copas**: YAML con fechas fijas, recurrentes automáticas o panel de administración.
