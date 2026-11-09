[EN](CAMERA_CONTROLS.md) | **ES**

# P19: cámaras, menú y ayuda en Android/PWA

Implementación del 8 de noviembre de 2026. API v1, minor **15**. Android y PWA usan los mismos endpoints, capacidades, cola de comandos y operaciones del Bridge. No se añadió otro transporte ni una API genérica de teclado.

## Evidencia y decisiones

- La [configuración oficial de GTBike V](https://www.gtbikev.com/content/configuration/) documenta `MenuKey` (F5), `NextCamKey` (`ZP_Left_Joy_Btn(U)`) y `LookBehindKey`. El fragmento de `GTBikeVScript.cs` 0.8.0.4 aportado por el usuario muestra que `NextCamKey` ejecuta `Game.SetControlValueNormalized(0, 1f)`. **Siguiente vista** reutiliza exactamente ese pulso, durante un solo frame.
- El mismo fragmento muestra que F5 alterna `Menus.Visible` y pone `MenuAutoOpenStatus = false`. Al pasar de inactivo a activo, el adaptador usa el cierre nativo `Menus.CloseMenus()` y después pone ese indicador a `false`. No hace nada adicional al recibir un estado que ya coincide ni al desactivar. **Esto implementa el efecto de ocultar del F5; no inyecta una pulsación física F5 del sistema operativo.** No existía una implementación de envío de teclas que reutilizar en los repositorios revisados. El cierre explícito evita abrir accidentalmente un menú ya oculto y también cierra los submenús.
- El código aportado muestra que `cameraRelativeHeading != 0` hace que el mod imponga el giro cada frame. Se lee ese campo, sin modificarlo, desde la misma instancia de script que ya resuelven las acciones nativas del menú. Si LookBehindKey está activo, el giro de Companion se deshabilita o se cancela. Si no se puede comprobar ese campo, no se habilita el giro.
- GTA expone [cinco valores de vista](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/CamViewMode.cs) y un [contexto específico OnBike](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/CamViewModeContext.cs). La selección directa usa [GET_CAM_VIEW_MODE_FOR_CONTEXT](https://github.com/citizenfx/natives/blob/master/CAM/GetCamViewModeForContext.md) y [SET_CAM_VIEW_MODE_FOR_CONTEXT](https://github.com/citizenfx/natives/blob/master/CAM/SetCamViewModeForContext.md) con contexto 2, en lugar del contexto por defecto de los coches. Cine usa [SET_CINEMATIC_MODE_ACTIVE](https://github.com/citizenfx/natives/blob/master/CAM/SetCinematicModeActive.md).
- La orientación usa las propiedades [GameplayCamera.RelativeHeading/RelativePitch](https://github.com/scripthookvdotnet/scripthookvdotnet/blob/main/source/scripting_v3/GTA/Camera/GameplayCamera.cs). Se interpola durante 450 ms por el arco más corto; cada trabajo termina o caduca en dos segundos. Reutiliza el host por frame del adaptador. No mantiene teclas pulsadas, no crea cámaras de script y no cambia dirección, freno, potencia ni resistencia.

## Contrato nuevo

`GET /api/v1/camera`, con el token existente:

```json
{
  "available": true,
  "reasonCode": null,
  "modes": ["near", "medium", "far", "firstPerson", "cinematic"],
  "currentMode": "medium",
  "headingDeg": 30,
  "pitchDeg": 0,
  "canLook": true,
  "lookReasonCode": null,
  "busy": false
}
```

Los tres comandos usan el sobre existente de `POST /api/v1/commands`: `requestId` UUID nuevo, `adapterInstanceId` observado y `expectedRevision: null`.

| Tipo | `args` | Efecto |
| --- | --- | --- |
| `camera.next` | `{}` | Un pulso del mismo control que NextCamKey. |
| `camera.setMode` | `{"mode":"far"}` | Selección directa de un modo anunciado por el PC. |
| `camera.look` | `{"headingDeg":90,"pitchDeg":-15}` | Giro suave; horizontal entre -180 y 180 grados, vertical entre -45 y 45. |

Capacidades nuevas: `camera.read`, `camera.next`, `camera.setMode`, `camera.look`. Error nuevo: `CAMERA_CONTROL_IN_USE`.

`applied` exige un resultado del adaptador etiquetado con el mismo `requestId` y modo/ángulos leídos de vuelta; una llamada al setter o un ACK no bastan. Las solicitudes repetidas conservan la deduplicación existente. Las aplicaciones nunca repiten automáticamente comandos tras una desconexión.

Los clientes anteriores siguen siendo compatibles: no cambió la estructura de `/state` ni el emparejamiento. Un cliente nuevo con Bridge anterior muestra que necesita actualizar el PC. Las listas de vistas y la selección salen del PC; se descartan identificadores desconocidos.

## Interfaz y preferencias

- En Rodar: vista observada, Siguiente vista, selector de vistas, izquierda/derecha, orientación, elevación y Centrar cámara. Los deslizadores envían al soltarlos. Los botones tienen al menos 48 dp/px y se distribuyen en varias filas cuando falta ancho.
- Cerca, Media y Lejana permiten giro. Primera persona y Cine conservan su orientación nativa; no se anuncia un control de órbita no verificado para esas vistas.
- Los iconos de ayuda empiezan ocultos. En Más → Ayuda aparece **Mostrar iconos de ayuda**. Android persiste `show_help_icons` con DataStore; PWA persiste `showHelpIcons` junto a sus preferencias existentes. Ambos valores son locales a su aplicación.
- Todas las ayudas siguen accesibles pulsando el título del tema en Todas las ayudas. Volver a mostrar los iconos restituye los botones originales y sus ventanas de ayuda.

## Validación y límites

Se probaron el protocolo contra un adaptador simulado, el controlador C# contra una cámara simulada, el cierre de menú contra el modelo de GTBike V y la PWA en navegador a 390 px. Se compiló el adaptador contra el `ScriptHookVDotNet3.dll` instalado y se generaron APK e instalador. El informe de entrega enumera las comprobaciones finales.

**Pendiente de validación dentro de GTA V:** comportamiento de cada vista en la instalación concreta, especialmente Cine, sentido de giro y límites reales que imponga el juego. Las pruebas simuladas y la compilación no acreditan esa ejecución. Si otra cámara de script tiene el control, cambia el contexto de cámara, se pierde control del personaje o el rodillo activa LookBehindKey, los controles se bloquean o el trabajo se cancela. No se fuerza la cámara de otro mod.

## Actualización y prueba manual

1. Cerrar GTA V y actualizar el PC con el instalador generado. Reiniciar el Bridge para que publique API minor 15. La PWA se ejecuta desde su repositorio; reiniciar su servidor y recargarla.
2. Instalar el APK de depuración generado, usando la misma firma que la copia de depuración existente. El APK no es una release firmada para publicación.
3. Activar el mod desde cada aplicación: comprobar que desaparecen los menús y que repetir el estado activo no vuelve a alternarlos.
4. Sobre una bicicleta, comparar Siguiente vista con el mando físico NextCamKey; cambiar también de vista desde el juego y verificar que ambas aplicaciones reflejan el cambio.
5. Probar Cerca/Media/Lejana, giro, elevación y centrado. Accionar LookBehindKey durante el giro: Companion debe cederle el control. Probar primera persona y Cine; la rotación de Companion debe quedar deshabilitada allí.
6. Desconectar el teléfono o pausar GTA: no deben quedar entradas retenidas. Reconectar no debe repetir la última orden.

Para compilar e instalar solamente el adaptador, con GTA V cerrado:

```powershell
Set-Location 'G:\develop2\games\GTBikeV\gtav-riding-companion-bridge'
.\bridge\Build-GameAdapterV1.ps1 -GtaDirectory 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced' -Install
```

Pruebas reproducibles:

```powershell
# Desde gtav-riding-companion-bridge:
npm test
.\bridge\Test-Camera.ps1
.\bridge\Test-GameAdapterV1.ps1

# Desde gtav-riding-companion-android:
.\gradlew.bat :core:test :bridge-client:test :app:testDebugUnitTest :app:assembleDebug :app:lintDebug :app:compileDebugAndroidTestKotlin

# Desde gtav-riding-companion-pwa:
npm test
npm run build
```
