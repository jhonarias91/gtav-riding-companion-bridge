[EN](README.md) | **ES**

# GTAV Riding Companion — Puente para PC

Un puente local para Windows que permite consultar y controlar desde un teléfono Android el mod [GTBike V](https://www.gtbikev.com/) de GTA V a través de la red Wi-Fi de casa. Ofrece métricas en vivo, recorridos, perfil y ajustes de ciclismo, fuentes de sensores, selección y progreso de entrenamientos, actividades guardadas (FIT), un mapa del juego y la clasificación.

Este repositorio contiene los componentes que se ejecutan en el PC. La app para el teléfono está en un repositorio aparte: **[gtav-riding-companion-android](https://github.com/jhonarias91/gtav-riding-companion-android/blob/HEAD/README_ES.md)**. Consulta la [guía de integración del puente Android](bridge/ANDROID_BRIDGE_V1_ES.md) para el empaquetado y las pruebas de aceptación.

> Proyecto comunitario no oficial y sin afiliación con GTBike V, Rockstar Games, Zwift ni Garmin. **No** incluye, modifica ni redistribuye `GTBikeV.dll`: añade un script independiente de ScriptHookVDotNet que se comunica con el mod que ya tienes instalado en tu PC.

## Instala en el PC

Descarga el instalador para Windows (Windows 10/11 de 64 bits). [Descargar GTAVRidingCompanion-Setup.exe](https://github.com/jhonarias91/gtav-riding-companion-bridge/releases/latest/download/GTAVRidingCompanion-Setup.exe) · [Todas las versiones para PC](https://github.com/jhonarias91/gtav-riding-companion-bridge/releases)

1. Cierra GTA V y ejecuta `GTAVRidingCompanion-Setup.exe`. El instalador no está firmado, así que Windows SmartScreen puede avisar: elige **Más información** → **Ejecutar de todas formas**.
2. En la pestaña **Setup**, revisa la carpeta de GTA V (se detecta sola) y pulsa **Install**. Windows pide permiso de administrador una vez.
3. Abre la pestaña **Phone** y escanea el código QR con la app Android.

Necesitas GTBike V 0.8.0.4 con ScriptHookV y ScriptHookVDotNet 3 ya instalados, y .NET Framework 4.8 (incluido en Windows 10/11). Node.js va incluido. Más detalles en [Inicio rápido](#inicio-rápido).

## Instala la app de Android

Descarga el APK en tu teléfono con Android 8.0 o posterior. [Descargar APK](https://github.com/jhonarias91/gtav-riding-companion-android/releases/latest/download/gtav-riding-companion.apk) · [Todas las versiones de Android](https://github.com/jhonarias91/gtav-riding-companion-android/releases)

1. Abre `gtav-riding-companion.apk` desde tus descargas.
2. Si Android te pide permiso para instalar apps de orígenes desconocidos, toca **Ajustes** y activa **Permitir desde esta fuente** para el navegador o gestor de archivos con el que abriste el APK.
3. Vuelve al instalador y toca **Instalar**.

Si quieres generar el APK, sigue la guía del repositorio Android. [Compila desde el ZIP del release](https://github.com/jhonarias91/gtav-riding-companion-android/blob/HEAD/README_ES.md#compila-desde-el-zip-del-release)

## Cómo funciona

```text
App Android (gtav-riding-companion-android)
        │  LAN privada, puerto 8788 · HTTPS automático con clave fijada (HTTP solo con --http-dev) · emparejamiento por QR y token Bearer
        ▼
Puente v1 de Node.js (bridge/start-v1.mjs)
        │  archivos JSON locales en %LOCALAPPDATA%\GTBikeVCompanion\v1 · comandos con caducidad y confirmación
        ▼
GTBikeVCompanionV1.dll — script para ScriptHookVDotNet 3 (bridge/game-adapter-v1)
        │  reflexión sobre miembros de GTBike V 0.8.0.4 cuyo uso se ha verificado
        ▼
GTBikeV.dll 0.8.0.4 → sensores ANT+ y Bluetooth, el rodillo y el juego
```

- El **adaptador** se ejecuta dentro de GTA V, en el hilo de scripts. Cada 250 ms escribe una instantánea del estado y aplica los comandos mediante los manejadores del mod, como los de las opciones del menú y los eventos nativos. Usa el rodillo y la instancia del mod que ya están activos.
- El **puente** valida cada comando con un contrato versionado (`bridge/contracts/v1`). Comprueba que el cambio se produjo antes de devolver `applied` y ejecuta cada solicitud como máximo una vez.
- **Los sensores permanecen conectados al PC.** El teléfono puede buscar sensores Bluetooth y pedir al PC que fije uno por dirección, pero nunca se conecta a ellos, así que los datos de la sesión no pasan por el teléfono.
- El contrato, el puente y el adaptador bloquean **el control manual del modo ERG, la potencia objetivo y las acciones de pausar, saltar o terminar entrenamientos** (`bridge/contracts/v1/policy.json`). Seleccionar un entrenamiento usa la misma acción *Select* del mod, y el mod activa ERG por sí mismo, igual que desde su menú.

## Requisitos

| Componente | Versión |
| --- | --- |
| Windows | 10 u 11 de 64 bits |
| GTA V | Probado en GTA V Enhanced con ScriptHookV y **ScriptHookVDotNet 3**. El adaptador se compiló y probó con la versión 3.9.0.0. |
| [GTBike V](https://www.gtbikev.com/) | Instala la versión **0.8.0.4** antes de configurar el puente. Sigue la [guía oficial de instalación de GTBike V](https://www.gtbikev.com/content/installation-guide/). El puente detecta otras versiones y deshabilita las operaciones de escritura. |
| .NET Framework | 4.8 o posterior. El adaptador lo usa durante la ejecución y la compilación desde el código fuente usa su compilador `csc.exe` de 64 bits. Windows 11 y Windows 10 22H2 ya incluyen una versión compatible. [Versiones incluidas con Windows](https://learn.microsoft.com/es-es/dotnet/framework/install/versions-and-dependencies) |
| Node.js | Solo para ejecutar o compilar desde el código: 22.2 o posterior (probado con 26.13). El instalador `.exe` incluye su propio runtime. |
| Teléfono | Android 8.0 o posterior con la [app GTAV Riding Companion](#instala-la-app-de-android), en la misma red local que el PC. El PC también puede conectarse por Ethernet. El escáner del QR de conexión requiere los servicios de Google Play. Si el teléfono carece de ellos, introduce la dirección y la huella de la clave manualmente. |

## Inicio rápido

### Instalador para Windows (recomendado)

Con GTA V cerrado, ejecuta `GTAVRidingCompanion-Setup.exe` ([descarga la última versión](https://github.com/jhonarias91/gtav-riding-companion-bridge/releases/latest/download/GTAVRidingCompanion-Setup.exe) o genéralo como se explica abajo). La pestaña **Setup** encuentra sola la carpeta de GTA V (Steam, Rockstar Launcher o Epic; si no, usa **Browse…**) y comprueba ScriptHookVDotNet3, GTBike V 0.8.0.4 y que el juego esté cerrado. Pulsa **Install** y acepta la solicitud de administrador de Windows: el instalador compila el adaptador para tu copia de ScriptHookVDotNet, lo instala (conserva las tres copias de seguridad más recientes) y crea una regla de firewall limitada a la red privada y la subred local para el runtime incluido. Después se abre sola la copia instalada. Añade accesos directos **GTAV Riding Companion** en el menú Inicio y, si quieres, en el escritorio, y una entrada en **Aplicaciones instaladas** de Windows para desinstalar. Al ejecutar un instalador más nuevo aparece **Update**, y **Uninstall…** quita el adaptador, la regla de firewall, los accesos directos y los archivos de la app, pero conserva tus vinculaciones y tus actividades.

La ventana (en inglés) tiene estas pestañas:

- **Phone**: código QR, código de vinculación manual, dirección, identidad del puente, huella de la clave y teléfonos vinculados. Déjala abierta mientras pedaleas.
- **Mod**: activar o desactivar GTBike V, perfil del ciclista, ajustes de pedaleo, bicicleta y personaje (las mismas opciones que la app Android) y un mapa en vivo con tu posición y el recorrido cargado. Rueda del ratón para acercar, arrastrar para mover, doble clic para acercar y clic derecho para **Teleport here** (solo en paseo libre; el juego te deja en el suelo o en la carretera más cercana), marcar un **destino GPS** (el juego dibuja la ruta en su minimapa y el mapa muestra la distancia por carretera) o copiar coordenadas. La imagen del mapa no va en el instalador: la primera vez pulsa **Download map (15 MB)**; se guarda en `%LOCALAPPDATA%\GTBikeVCompanion\map` y se comprueba (tamaño y dimensiones) contra la calibración de `installer\resources\gta-map.calibration.json` antes de usarla.
- **Routes**: al seleccionar un recorrido se muestran sus detalles, archivo y trazado en un mapa dentro de la misma pestaña. **Download map (15 MB)** comparte la imagen con Mod. El mapa conecta los puntos; GTA V calcula la trayectoria por carretera. El catálogo local también funciona sin iniciar el juego.

  **Create editable copy / Edit copy** abre directamente el editor en **Map**. Haz clic en un punto o en un segmento: se selecciona el waypoint más cercano y se resalta en azul. Mantén pulsado el botón izquierdo y arrástralo; el trazado acompaña el movimiento y la ubicación se actualiza al soltar. Arrastra una zona vacía para desplazar el mapa y usa la rueda para ampliar. Escape cancela un arrastre en curso.

  **New** crea una ruta desde cero: primer clic para la salida, clics siguientes para los puntos intermedios y la llegada. **Undo last point** deshace el último clic. Pulsa **Finish route** cuando hayas marcado la llegada y **Save route** para guardar. Basta una salida y una llegada; **Add points** permite prolongar después el trazado. Los puntos colocados o movidos en el mapa usan la altura del terreno que resuelve el juego. **Details**, **Waypoints** y **Advanced JSON** conservan las opciones de nombre, autor, descripción, notas, categorías, actividades, vuelta, estadísticas, coordenadas/altura, marcadores, props y otros campos.

  Las rutas personales se guardan en `Documentos\Rockstar Games\GTA V\ModSettings\CompanionRoutes`, con un ID propio y versiones por edición. Puedes guardar con el juego abierto. Con GTA V cerrado, **Add copies to game** incorpora los archivos al catálogo nativo `ModSettings\courses.json` y conserva una copia de seguridad; también se incorporan al volver a Routes con el juego cerrado. Inicia GTA V después para usarlas. La biblioteca personal se conserva si el mod reemplaza el catálogo oficial; puedes incorporarla de nuevo con el juego cerrado. **Apply route**, **Free ride** y **Show in game** mantienen los avisos del teléfono, incluido reiniciar un entrenamiento en curso.

- **Training**: planes y entrenamientos con su perfil de potencia, el entrenamiento en curso en el juego y **Select and start**. Pausar, saltar, terminar y ERG se hacen en el juego.
- **Sensors**: qué dispositivo usa GTBike V para cada función y su regla (Auto, Lock, Skip), y desconectar un sensor Bluetooth. Buscar un sensor Bluetooth nuevo se hace desde la app del teléfono.
- **Activities**: salidas guardadas con su resumen y gráfico de potencia, **Show file in folder** y **Save current activity**.
- **Game options**: las opciones de configuración de gtbikev.com que gestiona esta app (tráfico, público animando, bots del recorrido, autodefensa, sensibilidad de giro con teclas, puntos de paso, capturas, menú, reloj, paneles de depuración, nivel de registro, origen GPS, telemetría y multijugador). Se escriben en `GTBikeVConfig.ini` de GTBike V solo con GTA V cerrado (GTBike V reescribe ese archivo mientras está abierto), con una copia de seguridad en `%LOCALAPPDATA%\GTBikeVCompanion\bridge-v1\mod-config-backups`, y se aplican la próxima vez que inicie el juego. No se exponen asignaciones de teclas, identificadores de dispositivos ni direcciones de servidores. La app Android tiene la misma pantalla en **Más → Opciones del juego**.
- **Setup**: instalar, actualizar, reparar, desinstalar y abrir la carpeta de la app o el registro del adaptador.
- **Web**: la [PWA GTAV Riding Companion](https://github.com/jhonarias91/gtav-riding-companion-pwa) en cualquier navegador. **Download** descarga la rama `main` del repositorio en `%LOCALAPPDATA%\GTBikeVCompanion\pwa` (**Update** la reemplaza; **Use a local folder…** usa una copia local). **Start and open** inicia su servidor web con el Node.js incluido en la dirección Wi-Fi del PC (Ethernet si no hay Wi-Fi), en el puerto 4173 o el siguiente libre, inicia el puente si hace falta y abre el navegador ya vinculado: pasa el código de vinculación vigente como `#pair=CÓDIGO`, que la página quita de la dirección al vincularse. Un teléfono en la misma red puede abrir esa dirección y escribir el código de la pestaña **Phone**; la regla de firewall que crea **Install** solo cubre el puerto del puente.

Para generar el instalador desde el código en Windows, haz doble clic en `build-installer.cmd` o ejecuta:

```powershell
npm run build:installer
```

Este único paso instala las dependencias cuando hace falta (`npm ci`), ejecuta las pruebas, empaqueta el puente y compila `outputs\release\GTAVRidingCompanion-Setup.exe` (más un `.sha256.txt`). Opciones: `.\build-installer.cmd -SkipTests` para recompilar más rápido y `-Clean` para reinstalar `node_modules`. Necesita Node.js 22.2+ y el compilador de .NET Framework 4.8 de 64 bits. La primera compilación con cada versión de Node.js descarga su licencia y la guarda en `outputs\cache`; después funciona sin conexión. El ejecutable no incluye ni redistribuye GTBike V ni ScriptHookVDotNet.

### Desde el código fuente

Ejecuta los comandos en PowerShell desde la carpeta del repositorio, **con GTA V cerrado**. Sustituye la ruta del juego por la de tu instalación, donde está `ScriptHookVDotNet3.dll`.

1. Instala las dependencias y ejecuta las pruebas.

   ```powershell
   npm ci
   npm test
   ```

2. Compila el adaptador y cópialo a `GTA V\scripts`. Si el archivo ya existe, el script guarda primero una copia de respaldo con fecha.

   ```powershell
   $gta = 'C:\Program Files\Rockstar Games\Grand Theft Auto V Enhanced'
   .\bridge\Build-GameAdapterV1.ps1 -GtaDirectory $gta -Install
   ```

3. Opcionalmente, comprueba que tu copia de GTBike V sea compatible con el adaptador.

   GTBike V carece de una API pública, así que el adaptador busca por nombre las clases, campos y métodos internos del mod. El adaptador se escribió para GTBike V 0.8.0.4. En otras versiones, esos nombres pueden haber cambiado y las funciones que dependen de ellos pueden fallar. La comprobación abre `GTBikeV.dll` desde el disco y enumera los miembros que el adaptador necesita y que no encuentra. Puedes ejecutarla con el juego cerrado. Es una comprobación de solo lectura.

   La última línea muestra `RESOLVE PASS` si el adaptador encuentra los miembros que necesita. Si muestra `RESOLVE FAIL`, busca las líneas `Missing ... members` para identificar cuáles faltan.

   ```powershell
   .\bridge\Test-BindingResolve.ps1 -ModDll "$gta\scripts\GTBikeV.dll" -GtaDirectory $gta
   ```

4. Permite que la app Android acceda al puente a través del firewall de Windows. Ejecuta el comando una vez en PowerShell **como administrador**.

   ```powershell
   .\bridge\Enable-LanAccess.ps1 -Port 8788
   ```

   <details>
   <summary>Cómo funciona la regla del firewall</summary>

   La app se conecta al puente por la red local. Windows Firewall bloquea de forma predeterminada las conexiones entrantes desde otros dispositivos. Esta regla permite que el teléfono se conecte al puente.

   La regla permite conexiones entrantes:

   - Por TCP en el puerto 8788.
   - A `node.exe`, el programa que ejecuta el puente.
   - Desde dispositivos de la subred local.
   - Mientras el perfil de red de Windows sea **Privado**.

   La regla aparece en *Firewall de Windows Defender con seguridad avanzada → Reglas de entrada* como **GTAV Riding Companion Wi-Fi (8788)**. Para eliminarla, usa `-Remove`. Consulta [Desinstalación](#desinstalación).

   El script muestra `Firewall rule created: Node.js on TCP 8788, Private profile, local subnet only.` al crear la regla o `The bridge firewall rule already exists.` si ya existe.

   **Abre PowerShell como administrador.** En Inicio, haz clic derecho en *Terminal* o *Windows PowerShell*, selecciona *Ejecutar como administrador* y entra en la carpeta del repositorio.

   **Configura tu red de casa como privada.** En *Configuración → Red e Internet*, selecciona tu conexión Wi-Fi o Ethernet y cambia **Tipo de perfil de red** a **Privada**. Hazlo únicamente en redes de confianza. Puedes comprobar el perfil desde PowerShell.

   ```powershell
   Get-NetConnectionProfile   # NetworkCategory debe ser Private
   ```

   **Si el teléfono se conecta con un perfil público**, revisa las reglas existentes de Node.js. Si en algún momento permitiste el acceso de Node.js en redes públicas, Windows creó reglas llamadas **Node.js JavaScript Runtime** que permiten conexiones en esas redes desde cualquier dirección y por cualquier puerto. Puedes consultarlas con este comando.

   ```powershell
   Get-NetFirewallApplicationFilter -Program (Get-Command node).Source | Get-NetFirewallRule | Select-Object DisplayName, Action, Profile
   ```

   Configura la red de casa como privada, ejecuta el script del paso 4 y deshabilita las reglas públicas de *Node.js JavaScript Runtime* como administrador (`... | Where-Object Profile -eq Public | Disable-NetFirewallRule`). Puedes volver a habilitarlas con `Enable-NetFirewallRule`.

   **Si falla:**

   | Mensaje | Qué hacer |
   | --- | --- |
   | `Access is denied` / `Acceso denegado` | Abre PowerShell como administrador y vuelve a ejecutar el comando. |
   | `node` no se reconoce | Instala Node.js y abre una nueva ventana de PowerShell. Si el problema persiste, comprueba que Node.js esté en `PATH`. |
   | `running scripts is disabled on this system` | Permite ejecutar scripts en esa ventana con `Set-ExecutionPolicy -Scope Process Bypass` y vuelve a ejecutar el comando. |

   **Ten en cuenta:**

   - Si inicias el puente con otro `--port`, usa ese mismo valor en `-Port` al crear la regla.
   - La regla usa la ruta de `node.exe` que existía al crearla. Si reinstalas Node.js en otra carpeta, ejecuta el script con `-Remove` y luego vuelve a ejecutarlo para crear la regla.
   - La primera vez que inicies el puente, Windows puede pedir permiso para Node.js. Marca únicamente **Redes privadas** y elige **Permitir**. Si eliges *Cancelar*, Windows crea una regla de bloqueo que tiene prioridad. Para encontrar esas reglas, ejecuta lo siguiente como administrador.

     ```powershell
     Get-NetFirewallApplicationFilter -Program (Get-Command node).Source | Get-NetFirewallRule | Where-Object Action -eq Block
     ```

     Si aparecen reglas, revísalas. Puedes eliminar las que devuelve el comando añadiendo `| Remove-NetFirewallRule` al final.

   </details>

5. Inicia el puente y deja abierta la ventana.

   ```powershell
   node bridge/start-v1.mjs
   ```

   La consola muestra la identidad del puente, la huella de la clave, la dirección y el puerto del PC, los teléfonos emparejados con su fecha de caducidad y siempre un **código QR** para la app. Cada QR empareja un teléfono; después, pulsa Enter para mostrar uno nuevo. Enter también muestra un código de seis dígitos, que solo necesitas si escribes la dirección a mano en la app (caduca a los 5 minutos).

   El puente usa siempre HTTPS. La primera vez que se inicia, crea su clave en `%LOCALAPPDATA%\GTBikeVCompanion\tls\bridge-key.pem`. Conserva ese archivo porque la app confía en esa clave. Si lo borras, tendrás que escanear el QR de nuevo en cada teléfono.

   El emparejamiento dura 30 días. Cuando vence, la app pide escanear el QR de nuevo, y la consola muestra uno nuevo automáticamente en cuanto el teléfono intenta conectarse.

6. Inicia GTA V en modo Historia con GTBike V. En la app, toca **Escanear código QR** y apunta la cámara a la consola. No hace falta nada más: el QR lleva la dirección, el puerto, la huella de la clave, la identidad del puente y una clave de emparejamiento de un solo uso, así que el teléfono verifica el puente y se empareja sin escribir nada.

   Si quieres iniciar GTA V directamente en modo Historia, descarga y descomprime el mod. Con el juego cerrado, copia `StraightToStoryMode.asi` en la carpeta raíz de GTA V, donde está `ScriptHookVDotNet3.dll`. [Descargar Straight To Story Mode](https://www.gta5-mods.com/scripts/straight-to-story-mode)

### Opciones del puente

Puedes pasar estas opciones a `start-v1.mjs`.

```powershell
node bridge/start-v1.mjs --port 8788 --host 0.0.0.0 --map-directory D:\gta-map --enable-riders
```

| Opción | Efecto |
| --- | --- |
| `--port` | Puerto de escucha (8788 de forma predeterminada) |
| `--host` | `0.0.0.0` (valor predeterminado), `127.0.0.1` o una dirección IPv4 privada del PC |
| `--map-directory` | Carpeta con tu propio `map.png` y `calibration.json`. El formato está en la [guía del mapa del juego](bridge/ANDROID_BRIDGE_V1_ES.md#p8a--mapa-del-juego). |
| `--enable-riders` | Comparte la clasificación del recorrido actual con los teléfonos emparejados |
| `--http-dev` | Activa HTTP para desarrollo. Solo lo aceptan las compilaciones de depuración de la app. |

### Pasos adicionales

<details>
<summary>Usar tu propio certificado (opcional)</summary>

De forma predeterminada, el puente conserva una clave y regenera un certificado autofirmado en cada inicio con las direcciones actuales del PC. Si el router asigna otra IP, la app puede seguir verificando la identidad del puente mediante la clave.

Si quieres usar tu propio certificado y clave en formato PEM, configura ambas variables antes de iniciar el puente. El SAN del certificado debe incluir la dirección IPv4 del PC en la red local. Conserva la misma clave al renovar el certificado porque el teléfono verifica su huella.

```powershell
$env:GTBIKE_BRIDGE_TLS_CERT = "C:\path\to\bridge.pem"
$env:GTBIKE_BRIDGE_TLS_KEY  = "C:\path\to\bridge-key.pem"
node bridge/start-v1.mjs
```

El QR incluirá la huella de tu clave.

</details>

### Solución de problemas

```powershell
.\bridge\Diagnose-BridgeV1.ps1
```

El diagnóstico muestra la versión de Node.js, si el puerto está escuchando y la antigüedad y el estado de la instantánea del adaptador, incluida la versión del mod, la vinculación y el estado del control del juego. Nunca muestra el PIN ni los tokens.

- **El teléfono no llega al PC.** Conecta ambos dispositivos a la misma red, comprueba que el perfil de red de Windows sea *Privado* y que la regla del [paso 4](#inicio-rápido) esté activa. Revisa si alguna regla de bloqueo tiene prioridad. Las redes de invitados con aislamiento de clientes impiden el tráfico local. Prueba a abrir `https://<dirección-del-PC>:8788/api/v1/hello` desde el navegador del teléfono. Con el certificado autofirmado aparecerá un aviso. Si continúas y ves JSON, la conexión local funciona.
- **«Adaptador ausente».** Comprueba que GTA V esté abierto, que `scripts\GTBikeVCompanionV1.dll` esté instalado y que el puente y GTA V se ejecuten con el mismo usuario de Windows.
- **Cambió la IP del PC.** Reinicia el puente y escanea el QR de nuevo. La app seguirá confiando en la misma clave.
- **«El escáner QR no está disponible».** Si el teléfono carece de los servicios de Google Play, escribe la dirección, el puerto y la huella de la clave que muestra la consola.
- **«Port 8788 is already in use», o cerraste la ventana del puente pero el teléfono sigue conectado.** Un proceso anterior del puente sigue abierto y ocupa el puerto. Busca su identificador de proceso (PID), el número de la última columna:

  ```powershell
  netstat -ano | findstr :8788
  ```

  ```text
  TCP    0.0.0.0:8788           0.0.0.0:0              LISTENING       58480
  TCP    192.168.1.27:8788      192.168.1.181:48330    ESTABLISHED     58480
  ```

  Comprueba que el PID corresponde a `node.exe` y detén el proceso. Usa tu propio PID, no el de este ejemplo:

  ```powershell
  tasklist /FI "PID eq 58480"
  taskkill /F /PID 58480
  ```

  Después vuelve a iniciar el puente. Si el PID no es de `node.exe`, otro programa usa ese puerto: inicia el puente con otro `--port` y crea la regla del firewall para ese puerto.

### Desinstalación

Cierra GTA V y el puente. Después ejecuta estos comandos.

```powershell
Remove-Item "$gta\scripts\GTBikeVCompanionV1.dll"
.\bridge\Enable-LanAccess.ps1 -Port 8788 -Remove   # como administrador
```

Si quieres recuperar una versión anterior, restaura la copia de respaldo indicada por el instalador. Los datos de emparejamiento se guardan en `%LOCALAPPDATA%\GTBikeVCompanion\bridge-v1`.

## Instalación con el paquete (sin Git ni npm en el PC de juego)

Genera el ZIP desde una **copia limpia** del repositorio. El ZIP contiene el adaptador, el puente empaquetado y un manifiesto con el commit y el SHA-256 de cada archivo.

```powershell
npm ci
.\bridge\Pack-BridgeV1.ps1 -GtaDirectory $gta
```

Extrae el ZIP de `outputs\release` y ejecuta lo siguiente desde la carpeta extraída, con GTA V cerrado.

```powershell
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta -WhatIf
.\Install-BridgeV1Adapter.ps1 -GtaDirectory $gta
node .\bridge-v1.mjs
```

El instalador comprueba que GTA V esté cerrado y que `ScriptHookVDotNet3.dll` coincida con la DLL usada para compilar el paquete.

## Funciones

- **Sesión en vivo.** Consulta métricas y el estado del mod, del juego y de los sensores. Activa o desactiva el mod.
- **Recorridos.** Explora el catálogo, abre una vista previa en el PC e inicia un recorrido o una sesión libre.
- **Perfil y ajustes de ciclismo.** Peso, FTP, frecuencia cardíaca máxima, dificultad del rodillo, sensación de la carretera, piloto automático, bicicleta y personaje.
- **Sensores.** Selecciona la fuente para cada función (Auto, Lock o Skip), como en el menú *Device Selection* del mod. También puedes elegir un sensor Bluetooth desde el teléfono para que el PC se conecte a él.
- **Entrenamientos.** Consulta la biblioteca, selecciona un entrenamiento como en el menú del mod (sustituye al que esté en curso, después de un aviso en el teléfono) y sigue el progreso. Pausar, saltar y terminar se hacen en el PC.
- **Actividades.** Guarda la sesión como con la opción *Save* del mod. Lista, consulta el resumen y descarga archivos FIT.
- **Mapa y clasificación.** Consulta tu posición en un mapa calibrado del juego y comparte la clasificación del recorrido actual con `--enable-riders`.
- **Paquete y diagnóstico de Windows.** Instala el puente desde un ZIP en equipos sin Git ni npm. El script de diagnóstico funciona en modo de solo lectura.

## Resumen de la API

Todas las rutas salvo `hello` y `pair` requieren `Authorization: Bearer <token>`. El teléfono consulta `GET /api/v1/capabilities` para saber qué está disponible en cada sesión. Las funciones no disponibles incluyen un código de motivo. El valor `apiMinor` que devuelve `GET /api/v1/hello` identifica la revisión del contrato, actualmente la 11. La app Android actualizada envía un `clientId` aleatorio y persistente al vincularse; al escanear de nuevo, el bridge reemplaza su token anterior. Las versiones anteriores usan temporalmente el nombre del modelo: dos teléfonos del mismo modelo deben actualizar la app para mantenerse vinculados por separado.

| Método y ruta | Función |
| --- | --- |
| `GET /api/v1/hello` | Identidad del puente, versión de la API y transporte |
| `POST /api/v1/pair`, `POST /api/v1/unpair` | Emparejamiento (clave del QR o código de seis dígitos) y revocación de tokens |
| `GET /api/v1/capabilities` | Lecturas y cambios que admite la sesión actual, con códigos de motivo |
| `GET /api/v1/state` | Capas (puente, adaptador, mod y receptores), métricas y sensores |
| `POST /api/v1/commands`, `GET /api/v1/operations/{requestId}` | Comandos tipados y su ciclo de vida (`pending → applied / rejected / failed / unknown`) |
| `GET /api/v1/routes`, `GET /api/v1/route-preview` | Catálogo de recorridos y panel de vista previa en el PC |
| `GET /api/v1/settings` | Perfil, ajustes de ciclismo y apariencia |
| `GET /api/v1/device-roles` | Fuente de sensor y regla para cada función |
| `GET /api/v1/workout-plans`, `GET /api/v1/workouts`, `GET /api/v1/workout-progress` | Biblioteca y progreso de entrenamientos (la selección es el comando `workouts.select`) |
| `GET /api/v1/activities` | Actividades FIT guardadas, resúmenes y descargas |
| `GET /api/v1/map`, `GET /api/v1/riders` | Posición en el mapa del juego y clasificación opcional |

Los esquemas JSON están en [bridge/contracts/v1](bridge/contracts/v1). Los ejemplos válidos e inválidos están en [bridge/fixtures/v1](bridge/fixtures/v1).

## Desarrollo

```powershell
npm test                              # Node.js: API, contratos (Ajv), operaciones, política y FIT
.\bridge\Test-GameAdapterV1.ps1       # compila el adaptador con un GTBike V simulado y ejecuta las comprobaciones
.\bridge\Test-BindingResolve.ps1 -ModDll "$gta\scripts\GTBikeV.dll" -GtaDirectory $gta
```

Las pruebas automatizadas usan carpetas temporales y un adaptador simulado. Puedes ejecutarlas sin GTA V. El comportamiento dentro del juego se comprueba por separado con las listas de verificación de cada fase.

### Estructura del repositorio

| Ruta | Contenido |
| --- | --- |
| `bridge/api-v1/` | Servidor del puente v1 con emparejamiento, validación, operaciones, normalización, actividades, mapa y ciclistas |
| `bridge/game-adapter-v1/` | Adaptador de ScriptHookVDotNet escrito en C# 5 para GTBike V 0.8.0.4 |
| `bridge/contracts/v1/`, `bridge/fixtures/v1/` | Contrato JSON versionado, política de permisos y ejemplos |
| `bridge/compatibility/` | Notas de vinculación para GTBike V 0.8.0.4 con los miembros que usa el adaptador y la evidencia correspondiente |
| `bridge/*.ps1` | Scripts de compilación, instalación, empaquetado, firewall y diagnóstico |
| `tests/` | Pruebas de Node.js y de C# |

## Seguridad

<details>
<summary>Cómo protege el puente tu PC, tus datos y tus cuentas</summary>

- El puente sirve HTTPS (TLS 1.2 o posterior) de forma predeterminada. El teléfono fija la clave pública del puente, que obtiene del QR en la pantalla del PC, y rechaza cualquier otra. El QR también contiene una clave de emparejamiento de un solo uso (128 bits aleatorios): trátalo como el código al que sustituye y no compartas fotos de él. La clave cambia después de cada emparejamiento y en cada inicio del puente. `--http-dev` ofrece HTTP limitado a direcciones IPv4 privadas y de loopback, y solo lo aceptan las compilaciones de depuración de la app. Nunca redirijas el puerto a Internet.
- La clave privada del puente (`%LOCALAPPDATA%\GTBikeVCompanion\tls\bridge-key.pem`) se queda en el PC. No la compartas ni la subas a un repositorio.
- El emparejamiento usa la clave de un solo uso del QR o, si escribes la dirección a mano, un código de seis dígitos que dura 5 minutos. Ambos cambian después de cada emparejamiento, con un máximo de 5 intentos por IP y por minuto. El token del teléfono dura 30 días y el propio teléfono puede revocarlo. El puente guarda únicamente hashes de los tokens. Conserva el hash de un token caducado durante 90 días para que la consola identifique el teléfono que necesita un nuevo código.
- El puente valida los comandos con una lista de operaciones permitidas y ejecuta cada ID de solicitud como máximo una vez. La reflexión usa una lista fija de miembros del perfil `gtbikev-0804`. Las operaciones de escritura se deshabilitan con cualquier otra versión del mod.

</details>

## Próximos pasos

- **Configuración del mod desde Windows.** Llevar a la ventana los controles de perfil, ciclismo, apariencia y sensores que ya están disponibles a través de la API del puente para Android.

## Créditos y fuentes

- Documentación de [GTBike V](https://www.gtbikev.com/), incluidas las páginas de [configuración](https://www.gtbikev.com/content/configuration/) y [soporte](https://www.gtbikev.com/support/).
- [ScriptHookVDotNet](https://github.com/scripthookvdotnet/scripthookvdotnet) y [LemonUI](https://github.com/LemonUIbyLemon/LemonUI).
- [Garmin FIT JavaScript SDK](https://github.com/garmin/fit-javascript-sdk), disponible en npm como `@garmin/fitsdk`. Su licencia se incluye en el paquete de Windows como `GARMIN-FIT-SDK-LICENSE.txt`.
- [qrcode-generator](https://github.com/kazuhikoarase/qrcode-generator), de Kazuhiko Arase (MIT), genera el QR de conexión. Su licencia se incluye en el paquete de Windows como `QRCODE-GENERATOR-LICENSE.txt`. QR Code es una marca registrada de DENSO WAVE INCORPORATED.

## Licencia

Este proyecto se distribuye bajo la [licencia MIT](https://github.com/jhonarias91/gtav-riding-companion-bridge/blob/main/LICENSE).
