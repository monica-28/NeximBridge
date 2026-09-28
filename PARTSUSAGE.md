# Procesamiento de PARTSUSAGE

Worker guarda primero el cuerpo completo recibido en `nexim_raw_event` con estado
`PENDING`. El ID devuelto se pasa junto con `ParsedNeximMessage` a
`PartsUsageHandler.HandleAsync(message, rawEventId, cancellationToken)`.

`PartsUsageParser` interpreta los nueve campos de cabecera y NumList, que es el
décimo valor de esa misma cabecera. Los registros se separan por CR y cada uno
contiene exactamente doce campos. Se exige el CR final y que el número real de
registros coincida con NumList. Se valida todo el mensaje antes de persistir
detalles. Time se interpreta con formato `yyyyMMddHHmmss` y zona UTC.

`InsertPartsUsageDetails` inserta una fila por registro en
`dbo.nexim_component_event`, todas con el mismo `raw_event_id`. Usa una transacción
para el lote completo: un error SQL en un detalle revierte todos los detalles de
ese lote, conservando el raw previamente guardado. `quantity`, `feeder_id`,
`reel_id`, `old_reel_id`, `new_reel_id`, `vendor`, `lot_no` y `date_code` se envían
explícitamente como NULL. No se duplican SeqID ni el mensaje raw en los detalles.

Tras guardar el lote, el handler llama a `UpdateRawEventStatus(id, "PROCESSED")`.
Ante un error de parsing o persistencia, registra la excepción y trata de marcar
`ERROR` con un timeout independiente de diez segundos. Si SQL no está disponible
para actualizar el estado, se registra también ese fallo; el raw permanece
conservado y puede seguir en `PENDING`. La actualización de estado es posterior a
la transacción de detalles: si falla solo esa actualización, los detalles ya
confirmados permanecen guardados.

Se eliminó el overload antiguo que insertaba `part_number`, `detail_message`,
`seq_id`, `raw_message` y `received_at`. PARTSUSAGE usa exclusivamente su método
específico. El overload utilizado por otros eventos no se ha migrado en esta tarea.
No se alteraron el esquema, la configuración, el protocolo TCP, la inicialización,
KEEPALIVE, el simulador ni el envío de ACK de otros eventos.

## Pruebas en PowerShell

Desde la raíz del proyecto, con SDK .NET 8:

```powershell
dotnet build .\NeximBridge.csproj
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj
if ($LASTEXITCODE -ne 0) { throw 'Han fallado las pruebas PARTSUSAGE' }
```

El ejecutable de pruebas no requiere `dotnet test`. Las 20 pruebas sin SQL verifican
las dos filas NXT1, todos sus contadores, el mapeo SQL y los NULLs, UTC, NumList,
registros inválidos, estados y errores de persistencia. No conectan con Nexim.

Para añadir la prueba de las sentencias reales contra SQL Server, usando la cadena
existente de `appsettings.json`:

```powershell
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj -- --sql
```

Esta prueba adicional inserta el raw y dos detalles, comprueba cada columna y los
estados, y termina con ROLLBACK. No conserva filas ni modifica el esquema. Como
en cualquier INSERT de prueba con IDENTITY, puede consumir valores de identidad.
Las pruebas de estados del handler usan un repositorio simulado; la prueba SQL
ejecuta las mismas sentencias parametrizadas de inserción y actualización que
utiliza el repositorio, dentro de la transacción de prueba.
