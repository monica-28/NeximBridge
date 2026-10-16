# Cierre y reconexión TCP

NeximFrameReader distingue dos situaciones al recibir EOF:

- Sin ningún byte de una nueva cabecera: NeximConnectionClosedException (IOException).
- Con 1–3 bytes de cabecera, o con una cabecera completa pero un cuerpo incompleto:
  NeximFrameFormatException, indicando bytes recibidos y esperados.

Las validaciones de longitud, STX, ETX y UTF-8 permanecen intactas. La cancelación
se propaga como cancelación, sin transformarse en EOF o error de framing.

## Propiedad de la sesión

Worker es el único lector de la conexión. NeximTcpClientService ya no ejecuta un
sondeo Poll que pueda cerrar el socket mientras Worker está leyendo un frame.
El servicio publica una sesión y espera una señal de desconexión. Worker observa
EOF o un error, termina la lectura y retira ese stream mediante Disconnect(stream).

El cierre remoto limpio genera un log Information sin excepción ni stack trace.
Los frames realmente truncados o inválidos siguen generando Error con su excepción.
Los errores de transporte siguen retirando la conexión. La referencia del stream
impide que una notificación antigua desconecte una sesión nueva.

ConnectAsync está serializado mediante SemaphoreSlim, incluidas las llamadas
concurrentes. NetworkStream y TcpClient se disponen antes de liberar la sesión
para su sustitución. No se crean tareas de lectura en el servicio TCP; el Worker
registrado por el host conserva su bucle único de recepción.

Cada stream nuevo vuelve a pasar por SETEV → SETEV_ACK → STARTEV → STARTEV_ACK.
No se reutiliza la inicialización de la sesión anterior ni se altera su formato wire.

## Reintentos y apagado

Los retardos son 1, 2, 5, 10, 30, 30… segundos. Una conexión correcta reinicia la
secuencia: después de su desconexión se espera un segundo; si ese intento falla,
la siguiente espera será de dos segundos. La espera se cancela al detenerse el servicio.

ConnectionRefused y los errores habituales de disponibilidad generan Warning con
host, puerto y próximo retardo, sin repetir stack traces. Los errores inesperados
conservan Error con la excepción.

StopAsync cancela los intentos, dispone la sesión activa y espera a que un intento
de conexión en curso libere su TcpClient. Worker cancela sus lecturas y termina
su sesión en finally. No registra la cancelación normal como error ni reconecta
después de detener el servicio. Se comprueba también el orden inverso de parada:
si primero se detiene TCP, Worker reconoce ese estado y termina sin registrar el
ObjectDisposedException/IOException de cierre como un error de procesamiento.

## Persistencia y deduplicación

No se modifican parsers, handlers, repositorios, hashes, política de ACK ni SQL.
La reconexión no introduce ninguna caché. Las pruebas de retransmisión utilizan
el Worker y los handlers reales, NeximEventProcessor y el código RAW existente,
con transporte ADO.NET de prueba. Se verifica un RAW y un conjunto normalizado
tras dos conexiones; PARTSUSAGE responde de nuevo ACK=0 y LOADCOMPIV no responde.
Estas pruebas no escriben en SQL Server ni vuelven a implementar el algoritmo de hash.

## Archivos

Modificados:

- NeximFrameReader.cs: EOF tipado y distinción de truncamiento.
- NeximTcpClientService.cs: sesión única, cierre, backoff y cancelación.
- Worker.cs: tratamiento del EOF limpio y finalización de la recepción.
- tests/NeximBridge.PartsUsageTests/Program.cs: registro de las pruebas.
- tests/NeximBridge.PartsUsageTests/NeximBridge.PartsUsageTests.csproj: enlaces al código de conexión.
- tests/NeximBridge.PartsUsageTests/DeduplicationTests.cs: retransmisiones tras reconectar.

Creados:

- tests/NeximBridge.PartsUsageTests/ConnectionTests.cs
- TCP_RECONNECTION.md

No se modifican los escenarios ni el Program de NeximSimulator. Su build utiliza
el lector compartido actualizado, conservando los formatos y la secuencia existentes.

## Verificación desde PowerShell

```powershell
dotnet build
dotnet build .\NeximSimulator\NeximSimulator.csproj
dotnet run --project .\tests\NeximBridge.ProtocolTests
dotnet run --project .\tests\NeximBridge.PartsUsageTests
Get-ChildItem .\NeximSimulator\tests\test_*.py |
    Where-Object Name -ne 'test_keepalive_session.py' |
    Sort-Object Name | ForEach-Object {
        python $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Fallo: $($_.Name)" }
    }
```

Las pruebas C# de conexión usan puertos locales libres. Las pruebas Python usan
127.0.0.1:30040 y deben ejecutarse secuencialmente, con el puerto libre.
No se ejecutan las cinco pruebas históricas opcionales --sql, que escriben en SQL,
ni la prueba KEEPALIVE de aproximadamente nueve minutos. No se instala un servicio
Windows ni se establece una conexión con Nexim real.

Resultado: ambos builds correctos; 459/459 pruebas de eventos/conexión, 64/64 de
protocolo y 116/116 Python. Total: 639 comprobaciones superadas, con 30 casos nuevos.
El proyecto de tests de eventos conserva el aviso NU1900 de auditoría NuGet no
disponible; no impidió la compilación ni las pruebas.
