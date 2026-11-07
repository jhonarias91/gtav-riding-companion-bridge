[EN](ANDROID_BRIDGE_V1.md) | **ES**

# Puente Android v1 para Windows

La rama `android-v1` reúne el puente, el adaptador y el paquete de integración de P2 a P8, junto con la app Android en `gtav-riding-companion-android` (misma rama). Para decidir qué mostrar o permitir, la app consulta `GET /api/v1/capabilities`. El valor `apiMinor` identifica la revisión del contrato del puente. Durante las fases P2 a P8 correspondió a los valores 0 a 6. Las capacidades describen las funciones disponibles en cada sesión.

## Compatibilidad y estado

| Componente | Versión comprobada | Evidencia |
| --- | --- | --- |
| GTBike V | AssemblyVersion 0.8.0.4, perfil `gtbikev-0804` | Firmas de mapa y ranking resueltas por reflexión |
| ScriptHookVDotNet3 | AssemblyVersion 3.9.0.0, referencia de la instalación GTA V Enhanced | Compilación del adaptador x64 con csc de .NET Framework |
| Node.js | 26.13.0 | Pruebas de HTTP y calibración por loopback |
| GTA V Enhanced y teléfono | Pendiente | Completar el checklist de aceptación con el juego y el teléfono |

El paquete identifica el commit, la DLL de referencia y el SHA-256 de cada archivo. El canal de integración requiere Node.js 22.2 o posterior y se probó de forma automatizada con Node.js 26.13.0. El puente usa HTTPS de forma predeterminada. La opción `--http-dev` activa HTTP y solo la aceptan las compilaciones de depuración de la app. La distribución para Android requiere verificar la identidad del PC por HTTPS (incremento 2b) y completar las pruebas de aceptación en el juego de P2 a P8.

## Empaquetar, instalar y arrancar

Desde un checkout limpio de la rama `android-v1`, con GTA V cerrado:

```powershell
npm ci
$gta = 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced'
.\bridge\Pack-BridgeV1.ps1 -GtaDirectory $gta
```

Extrae el ZIP de `outputs\release` y abre PowerShell en la carpeta extraída. Compara su SHA-256 con la salida del empaquetado. Ejecuta la instalación con la misma cuenta de Windows que usará GTA V.

```powershell
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta -WhatIf
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta
node .\bridge-v1.mjs --host 127.0.0.1
```

Comprueba el puerto de loopback antes de usar el teléfono. Para una prueba de integración en una red privada, abre el puerto 8788 con `Enable-LanAccess.ps1 -Port 8788` desde PowerShell como administrador. Arranca el puente con una IPv4 privada del PC que la consola pueda anunciar y empareja el teléfono. El servidor se ejecuta con el usuario de GTA V y ambos usan `%LOCALAPPDATA%\GTBikeVCompanion\v1` para intercambiar instantáneas de estado y comandos.

Android v1 usa el puerto 8788 y su propio emparejamiento. Los tokens de Android se guardan en el directorio `bridge-v1`; usa el PIN actual en la consola para emparejar un teléfono nuevo. Anota el nombre del respaldo que imprimió el instalador.

## Volver a la versión anterior

Detén el puente, cierra GTA V y usa el respaldo exacto de la instalación. Sustituye el ejemplo por la ruta impresa:

```powershell
$scripts = Join-Path $gta 'scripts'
$target = Join-Path $scripts 'GTBikeVCompanionV1.dll'
Copy-Item -LiteralPath 'RUTA-EXACTA-DEL-RESPALDO.bak' -Destination $target -Force
```

Si fue la primera instalación de v1, mueve `GTBikeVCompanionV1.dll` fuera de `scripts`. Arranca el paquete anterior si lo tenías. Para retirar la regla LAN de v1, usa `Enable-LanAccess.ps1 -Port 8788 -Remove` como administrador.

## P8a · Mapa del juego

Prepara un directorio local con un `map.png` que tengas permiso de compartir en tu LAN y un `calibration.json`:

```json
{
  "version": 1,
  "source": "Origen de tu imagen",
  "rights": "Permiso aplicable",
  "sha256": "<sha256 en minúsculas de map.png>",
  "anchors": [
    { "x": 10, "y": 10, "px": 10, "py": 90 },
    { "x": 90, "y": 10, "px": 90, "py": 90 },
    { "x": 10, "y": 90, "px": 10, "py": 10 }
  ],
  "checkpoints": [
    { "x": 50, "y": 50, "px": 50, "py": 50 },
    { "x": 80, "y": 80, "px": 80, "py": 20 }
  ]
}
```

Los números son un ejemplo sintético para una imagen de 100×100. Sustitúyelos por tres referencias repartidas en el mapa y al menos dos comprobaciones independientes. `x/y` son coordenadas del mundo GTA y `px/py` se miden desde la esquina superior izquierda del PNG. El hash se obtiene con `(Get-FileHash -LiteralPath .\map.png).Hash.ToLowerInvariant()`. La validación exige que cada comprobación quede a 4 px o menos de la proyección.

Arranca con `--map-directory RUTA`. `GET /api/v1/map` requiere un token y devuelve la posición y los puntos del recorrido activo en coordenadas normalizadas. Un punto nulo corta el trazo o indica que la posición queda fuera de la cobertura. Los segmentos unen waypoints del mod. El cliente descarga el PNG con el mismo token en `GET /api/v1/assets/{assetId}`. Si cambias la imagen o la calibración, reinicia el puente. El mapa real queda pendiente de contar con un PNG autorizado y verificar las referencias en GTA.

## P8b/P8c · Clasificación y ajustes avanzados

Activa `--enable-riders` para compartir con clientes emparejados el ranking del curso actual o del candidato nativo cuando se rueda libre. `GET /api/v1/riders` separa el estado de la conexión multijugador del PC y distingue ciclista local, humanos y bots según el tipo de fila del juego. Las respuestas llevan nombre visible y lecturas del ranking. La sugerencia `busiestRoute` requiere al menos un humano remoto en el destino elegido por el mod.

La orden remota de unirse queda deshabilitada. `JoinMultiplayerHandler` llama a `LoadCourse`, que puede esperar una descarga. Su evento deja la inicialización para un Tick posterior. Falta un punto de control probado para evitar un cambio de sesión si en ese intervalo arranca un workout desde el PC. La selección se hace en el menú de GTA V.

La edición de bots y tráfico también queda deshabilitada. La configuración se lee de la caché del mod. `BotGang.CreateBotGang` consulta la definición al crear un grupo y `ChangeCarsForBikes` captura sus ajustes al construirse. Para habilitar P8c hace falta probar una aplicación posterior a la recarga, con revisión, respaldo y distinción entre valor persistido y valor en uso.

## P18 · Complementos (Alleycat, sprint de supervivencia, segmentos fantasma, Descubre) y XP del ciclista

Modos de juego opcionales que ejecuta el adaptador junto a GTBike V (`GtbikeV1Addons.cs`; `GtbikeV1AddonHost` es un segundo script con `Interval = 0`, porque los golpes duran un frame y el marcador se dibuja en cada frame). Solo nativas de GTA; no se escribe nada de GTBike V. GTBike V lee el viento del juego (`GET_WIND_SPEED`/`GET_WIND_DIRECTION`) y el clima (fricción y tracción), así que el viento en contra llega al rodillo como resistencia y la lluvia reduce el agarre. Teléfonos, PWA y ventana del PC usan el mismo contrato (`contracts/v1/addons.schema.json`):

- `GET /api/v1/addons`: registro con descriptores de parámetros tipados (`integer|number` con `min/max/step/unit`, `boolean`, `enum` con `options`), `value`, `editable`, `state`, `run` (fase, reloj, `stats` y `nextPoint`) y `result`. Un cliente dibuja un control por descriptor; un complemento nuevo no requiere cambios en los clientes. `addonsRevision` solo cambia cuando cambia un valor.
- `addons.setEnabled` `{addonId, enabled}`: iniciar exige `expectedRevision = addonsRevision`, mod activo, juego en control, sin recorrido (`VALUE_NOT_ALLOWED`) y ciclista en bicicleta (`RIDER_NOT_ON_BIKE`); detener lleva `expectedRevision: null` y siempre se permite. Sin puntos en el centro: `ADDON_NO_POINTS`.
- `addons.configure` `{addonId, params}` (solo los cambiados, `expectedRevision = addonsRevision`); los parámetros con `liveEdit: false` no cambian con el complemento en marcha. El adaptador los guarda en `v1ddons-settings.json`.
- Un complemento a la vez (`ADDON_BUSY`). Alleycat no se permite en un recorrido; el sprint de supervivencia sí.
- Sprint de supervivencia: al superar `targetKmh` aparece un perseguidor a `startGapM` sobre tu propia trayectoria; se mantiene si sostienes la velocidad pedaleando, se acerca si vas más lento o dejas de pedalear más de `coastGraceS` (potencia y cadencia de GTBike V) y te embiste a 4 m. Gana quien aguanta `durationMin`.
- Segmentos fantasma: `addons.saveSegment` (crear/renombrar; mover un punto reinicia el récord; máx. 50) y `addons.removeSegment`; `segments[]` en el documento del complemento y en `/desktop/world`. Al entrar en un círculo de salida empieza el intento y un ciclista fantasma repite tu mejor (o último) intento; `deltaS` es la diferencia en vivo. Archivos `v1/ghost-segments.json` y `v1/ghosts/`. El teletransporte solo se bloquea durante un intento.
- Descubre: lugares con `addons.savePlace` / `addons.removePlace` (`places[]`, máx. 100). Cada ronda esconde un lugar o un punto aleatorio; solo se expone la pista (zona, distancia, frío/caliente, tendencia), nunca su posición. XP por hallazgo (distancia, rapidez, primera visita, zona nueva, racha diaria, sin círculo).
- XP del ciclista: archivo local `v1/rider-progress.json`. Completar un recorrido de GTBike V (evento público `Course.OnCourseCompleted`, solo escucha) da XP y lo marca como completado; `GET /api/v1/progress` (`progress.read`) devuelve XP, nivel, racha y `routes[]` con veces y mejor tiempo.
- Con un complemento en marcha se rechaza `player.teleport`; cargar un recorrido termina la misión. `/desktop/world` añade `addonPoints`.

## Diagnóstico y aceptación

- Ejecuta `Diagnose-BridgeV1.ps1` para comprobar el puerto, la versión detectada y la antigüedad de la instantánea de estado. Comparte el resultado sin PIN ni token. Un puerto en escucha demuestra que algún proceso lo abrió. Comprueba que `/api/v1/hello` identifica el puente esperado.
- En el teléfono, verifica la identidad del PC, emparejamiento, caducidad, revocación, reconexión y cambio de IPv4. Anota la versión de Android y el SHA-256 del paquete usado. Para distribución, ejecuta estas pruebas por HTTPS y comprueba el rechazo de otro certificado y de HTTP.
- Con GTA cerrado, comprueba que la app identifica el adaptador como ausente. Con el juego activo, verifica el estado, las métricas, los recorridos y los ajustes. Continúa con las listas de verificación de P3 a P8 (`planning/android/P*_*.md`).
- Si activas P8, compara varios puntos del mapa con GTA y la clasificación con su HUD. Prueba el recorrido inverso, la salida de la cobertura, la pérdida del multijugador y una instantánea obsoleta.
- Registra una sesión continua de 60 minutos con cambios de Wi-Fi, paso a segundo plano y reconexión. Comprueba que los cambios se apliquen una sola vez y que la app identifique las lecturas obsoletas. Para cerrar P9 faltan también la APK firmada y la revisión EN/ES en Android.
