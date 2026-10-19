# SysDVR+

Cliente de PC para **SysDVR**, pensado para jugar a la Switch Lite en el monitor con la mejor calidad posible y ver de un vistazo cómo va el rendimiento.

El sysmodule de la consola es el original, sin tocar. Este repositorio solo contiene el cliente, así que funciona con la versión oficial de SysDVR.

## Qué cambia respecto al cliente original

- **Salida hasta 4K**: 1080p, 1440p y 4K con filtros de escalado (nearest, linear, bicubic, lanczos y lanczos suave), antialias y enfoque opcionales. El escalado y el enfoque van en CPU multihilo: a 1080p cuesta unos 4-5 ms por fotograma.
- **Perfiles con un clic**: Calidad, Equilibrado y Baja latencia. El de baja latencia desactiva el postproceso y quita el límite de fotogramas.
- **OSD con F3**: FPS, coste real del pipeline en milisegundos (con pico), uso de CPU y GPU, bitrate de vídeo y audio, búfer, paquetes perdidos y una gráfica de frametime.
- **Modos de presentación**: VSync, adaptativo o sin límite, más la opción de estirar la imagen a toda la pantalla.
- **Modo rápido (GPU 720p)**: se salta el escalador de la CPU y deja que la tarjeta gráfica estire el fotograma a la ventana. Más suave, pero ideal para 4K o equipos justos.
- **Reconexión automática**: 5 intentos si se cae el enlace, y la tecla **C** para reconectar a mano cuando quieras.
- **Selector de decodificador** en el propio panel: d3d11va, cuda, qsv, amf, mf, etc.
- **Interfaz propia**: tema oscuro con acento cian, icono nuevo y todos los ajustes guardados en `options.json`.

## Instalación

Hacen falta dos cosas: el sysmodule en la consola y este cliente en el PC.

### 1. Sysmodule (repositorio oficial)

Descarga **SysDVR v6.3** desde la página de releases del proyecto original:

https://github.com/exelix11/SysDVR/releases

Usa el archivo `SysDVR.zip` (el `USB-Only.zip` es solo si no quieres streaming por WiFi) y copia su contenido a la raíz de la tarjeta SD de la consola. Enciende la consola una vez para que arranque el sysmodule.

### 2. Cliente

Descarga `SysDVR-Plus-2.1-win-x64.zip` desde la sección **Releases** de este repositorio, descomprímelo donde quieras y ejecuta `SysDVR-Plus.exe`.

### 3. Conectar

1. Conecta la consola por USB (sin dock) o por WiFi y elige el modo en la pantalla principal.
2. Pulsa **Transmitir**. Si el USB no aparece la primera vez, usa el botón **Driver USB** de la propia aplicación.
3. Teclas: **V** abre y cierra el panel de vídeo, **F3** muestra el OSD y **C** reconecta.

Requisitos: Windows 10/11 de 64 bits, firmware 6.0.0 o superior y un juego que permita grabar vídeo (mantén pulsado el botón de captura para comprobarlo).

## Lo que conviene saber

- La consola captura **siempre a 720p y 30 fps**. Es un límite del hardware. El 1080p, 2K y 4K son escalado en el PC: la imagen llena el monitor y se ve más limpia, pero no aparece detalle que no estuviera en la captura.
- A 1080p con lanczos y enfoque el proceso cuesta unos 4-5 ms de los 33,3 ms que hay por fotograma. A 4K sube a unos 11-12 ms.
- El USB no funciona con la consola en el dock.
- Solo se captura el juego. El menú de la consola y los homebrew que corren como applet no salen.

## Compilar

```
dotnet publish Client/Client.csproj -c Release -r win-x64 -p:SysDvrTarget=windows --self-contained
```

La versión que muestra la aplicación se lee de `Client/release_version.txt`.

## Créditos y licencia

- **SysDVR**, el sysmodule y el cliente original son obra de **exelix11**: https://github.com/exelix11/SysDVR. Sin su trabajo este proyecto no existiría.
- **SysDVR+** (este cliente) está hecho por **Bernar Millan**: https://github.com/bernarmillan

Se distribuye bajo **GPL-2.0**, la misma licencia que el proyecto original. Se mantienen los avisos de copyright del código de base.

---

# English

SysDVR+ is a PC client for **SysDVR** focused on playing Switch Lite games on a monitor with client side upscaling and a clear view of the performance.

The console sysmodule is untouched, so this works with the official SysDVR release.

## What is different

- **Output up to 4K**: 1080p, 1440p and 4K with nearest, linear, bicubic and lanczos filters, optional antialias and sharpening. Scaling and sharpening run on a multithreaded CPU pass, about 4-5 ms per frame at 1080p.
- **One click presets**: Quality, Balanced and Low latency.
- **F3 overlay**: FPS, real pipeline cost in milliseconds (with peak), CPU and GPU usage, video and audio bitrate, buffer, dropped packets and a frametime graph.
- **Presentation modes**: VSync, adaptive or uncapped, plus a fill screen option.
- **Fast mode (GPU 720p)**: skips the CPU scaler and lets the graphics card stretch the frame to the window.
- **Auto reconnect**: up to 5 attempts, and **C** to reconnect manually.
- **Decoder selector** in the video panel.
- **Own look**: dark theme with a cyan accent, new icon and settings saved to `options.json`.

## Install

1. Install the official sysmodule: download **SysDVR v6.3** (`SysDVR.zip`) from https://github.com/exelix11/SysDVR/releases and copy it to the root of the console SD card.
2. Download `SysDVR-Plus-2.1-win-x64.zip` from the Releases page of this repository and run `SysDVR-Plus.exe`.
3. Connect the console over USB (undocked) or WiFi, pick a mode and press Stream. On Windows, use the USB driver button in the app if the console is not detected.
4. Keys: **V** video panel, **F3** overlay, **C** reconnect.

Requirements: Windows 10/11 x64, firmware 6.0.0 or newer, and a game that allows video recording.

## Notes

- The console always captures at **720p 30 fps**, that is a hardware limit. 1080p, 2K and 4K are upscaled on the PC: the image fills the screen and looks cleaner, but no extra detail is added.
- At 1080p the pipeline costs about 4-5 ms out of the 33.3 ms available per frame. At 4K it is around 11-12 ms.
- USB streaming does not work while the console is docked.
- Only game output is captured, the home menu and applet homebrew are not.

## Build

```
dotnet publish Client/Client.csproj -c Release -r win-x64 -p:SysDvrTarget=windows --self-contained
```

The displayed version comes from `Client/release_version.txt`.

## Credits and license

- **SysDVR**, its sysmodule and the original client are made by **exelix11**: https://github.com/exelix11/SysDVR
- **SysDVR+** (this client) is made by **Bernar Millan**: https://github.com/bernarmillan

Licensed under **GPL-2.0**, same as the original project. Original copyright notices are kept.
