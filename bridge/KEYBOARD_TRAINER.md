# Rodillo de prueba con teclado

El escenario Android «Desarrollo · Listo, sin sensores» es una vista previa de datos de ejemplo del teléfono. No conecta un rodillo al PC ni mueve el personaje. El modo de teclado se ejecuta en el adaptador de GTA y funciona sin teléfono. Para ver la rodada en Android, conecta la app al PC mediante Bridge.

## Uso

Con GTBikeV 0.8.0.4 y el adaptador actualizado cargados, deja desactivado el mod y desconecta los sensores físicos. Cierra los menús y pulsa **F10** dentro de GTA V para activar el modo; el HUD indica que el pedaleo es simulado. Mantén **W** para aportar potencia y 85 RPM. Al soltar W, la potencia y cadencia pasan a cero y la bicicleta conserva su inercia. **S** frena y **A/D** mantienen la dirección habitual del juego. Pulsa **+ / −** (también en el teclado numérico) para ajustar la potencia en pasos de 25 W. **F10** desactiva el modo y descarta su actividad de prueba.

La potencia comienza en 200 W y admite entre 50 y 1000 W. La app recibe `testInput.mode = "keyboard"` y muestra un aviso de datos simulados. Si usas la app del teléfono, conéctala al PC; las opciones de vista previa de Desarrollo no activan este modo. La sección Mod de la ventana del PC también muestra las instrucciones y su estado.

Espera a que el mod monte al personaje en la bicicleta. Mientras se monta, está pausado, hay menús abiertos o GTA no tiene el foco, W no genera pedaleo. Al salir se detiene la bicicleta, se cancelan sus tareas de conducción y se restaura la invulnerabilidad y el nivel de búsqueda anteriores a la prueba.

Para compilar el adaptador con el juego cerrado:

```powershell
.\bridge\Build-GameAdapterV1.ps1 -GtaDirectory 'G:\SteamLibrary\steamapps\common\Grand Theft Auto V Enhanced' -Install
```

El comando crea una copia de respaldo del adaptador anterior. No modifica `GTBikeV.dll` ni el INI. Para actualizar también Bridge y la ventana del PC, compila `npm run build:installer` y ejecuta el instalador generado. Instala la nueva APK para ver el aviso en Android.

## Implementación y alcance

`GtbikeV1KeyboardTrainer` usa exclusivamente la estructura comprobada de GTBikeV 0.8.0.4. Verifica los miembros necesarios antes de cambiar el estado. Rechaza la activación si el mod está inicializado, los receptores siguen activos o en transición, existe actividad/recorrido, o `DeviceInfo.GetCurrentDeviceFor` devuelve un sensor físico.

No llama a `DataTransceiver.Initialize`, `ActivateModHandler`, ni a los métodos de inicio de los receptores. Mientras está activo, mantiene `DTInitialized = false` y suspende únicamente el temporizador nativo `DATA_UPDATE`. El estado interno `BLEStatus = READY` permite que el bucle de conducción nativo consuma las muestras sintéticas. El informe del adaptador sustituye ese estado por los estados físicos anteriores y no anuncia un dispositivo BLE ficticio.

El `onTick` del mod siempre consulta ANT, incluso sin un dispositivo. Para evitar que esa lectura convierta en cero la potencia utilizada por la física, la simulación conserva un clon independiente de `VirtualTrainer`. Cada fotograma posterior al `onTick` actualiza ese modelo con potencia, cadencia, peso, bicicleta, pendiente, rozamiento, tracción, viento y frenado; publica su velocidad para el siguiente fotograma nativo. Se usa `MemberwiseClone`, sin ejecutar otra vez el constructor que modifica la función de cálculo de `Config.VirtualGears`.

El clon se actualiza con las marchas virtuales desactivadas: no emula un dispositivo de resistencia ni comprueba el comportamiento ERG de un rodillo físico. Tampoco genera frecuencia cardíaca. La distancia se integra desde la velocidad simulada y el tiempo transcurrido, con los reinicios normales de actividad del mod. Pausa, pérdida de foco y saltos largos de tiempo no generan pedaleo ni un salto de velocidad.

Cada actividad creada durante la prueba recibe un límite de guardado infinito en su campo de instancia `MINIMUM_DISTANCE_TO_SAVE_ACTIVITY` y `telemetryReporting = false`. Así, los caminos nativos de `Encode` al guardar, cambiar recorrido o desactivar el mod no escriben FIT ni envían estadísticas de esa prueba. Estos cambios no escriben el archivo INI. Al detenerse se descarta la actividad y se restauran los campos estáticos del transceptor, el objeto original del entrenador, sus receptores y el temporizador; no se llama a `Encode`.

## Validación

Validación local del 8 de noviembre de 2026: adaptador y programa de instalación compilados; 83 pruebas Bridge/Windows correctas; arnés con 34 comprobaciones que usa la física de la referencia 0.8.0.4; comprobación de miembros contra la DLL original correcta. Android: 123 pruebas JVM correctas y 3 TLS omitidas, 21 pruebas de interfaz correctas, lint sin errores (42 advertencias). Los PNG de verificación Windows están en `outputs/ui-keyboard`.

La paridad Android/Windows queda registrada en `AGENTS.md`. Windows incorpora los controles de cámara, métricas de rodada con su procedencia, tabla de ciclistas y diagnósticos con copia del informe. Las dos aplicaciones usan las mismas operaciones y motivos de indisponibilidad del Bridge.

La lectura de referencia es estática: `DataTransceiver.Update`, `VirtualTrainer.Update`, `HandleBraking`, `GTBikeVScript.onTick`, `Activity.Encode` y `Timer.Update` de la versión 0.8.0.4. La compilación y las pruebas de física con un arnés no sustituyen una prueba dentro de GTA.

Comprobaciones manuales dentro del juego:

1. Activar desde el estado inactivo sin sensores; comprobar el aviso de simulación y la bicicleta.
2. Mantener W y comprobar potencia, cadencia, aceleración y métricas de la app conectada al PC.
3. Soltar W; comprobar cero W/RPM con inercia. Frenar con S y girar con A/D.
4. Pausar o cambiar de ventana; comprobar que no se mantiene pedaleo ni hay un salto de velocidad al regresar.
5. Probar recorrido/cámara y detener el modo; comprobar que no aparece una actividad FIT de prueba.
6. Comprobar que se rechaza la activación con el mod/sensores reales activos y que luego funciona la activación normal.
