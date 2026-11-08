# Instrucciones del proyecto

## Commits

- Los mensajes van en español, en una sola línea corta que diga qué efecto causa el cambio en el código.
- Cada mensaje empieza con uno de estos prefijos:
  - `Fix:` para fixes.
  - `Refactor:` para cambios que no modifican el comportamiento visible al usuario, pero sí la forma en que las cosas funcionan por dentro.
  - `Feature:` para features.
  - `Style:` para cambios de UI.
  - `Tests:` para pruebas y testing.
  - `Docs:` para documentación.
  - `Other:` para otros cambios.
- Agrupar los cambios en commits separados por tema (no un solo commit con todo).
- Commitear en local; no subir (`git push`) salvo que se pida.

## Online de PES 2013

- Antes de tocar el modo online de PES 2013, leer `docs/reverse-engineering/pes2013-online-structure.md`: explica las tres vías del cliente (sockets, HTTP por `CMD_GET_URLLIST`, UDP/STUN/P2P), el ciclo de sesión, el mapa de handlers y cómo investigar un caso nuevo.
- El decompilado completo de `pes2013.exe` está en `C:\VSS\Pes2013Decompilado\export\all_functions.c` (con índices de strings y el proyecto Ghidra en esa misma carpeta). Usarlo para confirmar claves, comandos y tipos de URL en vez de adivinar.
- No desplegar a `C:\Servers\PES2013` sin que el usuario lo pida: el reinicio corta a los jugadores conectados.
