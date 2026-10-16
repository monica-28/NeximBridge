# Eventos de componentes y alarmas

LOADCOMPIV, CHANGECOMPII y UNLOADCOMP tienen parsers y handlers específicos.
La cabecera contiene siete campos TAB, incluido NumList, y termina en CR.
Los detalles contienen respectivamente 11, 7 y 7 campos, cada uno terminado en CR.
Se valida todo el mensaje antes de insertar detalles: cantidades y contadores
no negativos, SupplyMethod 0/1, hora estricta `yyyyMMddHHmmss` interpretada como UTC.

MCALARMON y MCALARMOFF contienen ocho campos TAB, sin CR. Ambos códigos de error
deben contener exactamente ocho caracteres hexadecimales; se conservan literalmente.
No se inventan descripciones. Los campos SQL ajenos al evento se insertan como NULL.

El Worker guarda primero el RAW con PENDING. Los handlers reciben su raw_event_id,
insertan los datos normalizados y marcan PROCESSED. Un fallo marca ERROR con un
token independiente y conserva raw_message. Los nuevos repositorios ejecutan sus
comandos dentro de una transacción; un error revierte todo el lote de detalles.
El RAW está fuera de esa transacción. Si la actualización de estado también falla,
se registra el error y se mantiene la excepción original.

## Preparación SQL manual

Revisar [database/remaining_component_events.sql](database/remaining_component_events.sql)
y aplicarlo manualmente a la base local antes de probar los nuevos eventos de componentes.
Sólo añade, mediante comprobaciones COL_LENGTH:

- lighting_class VARCHAR(100) NULL
- supply_method INT NULL
- remaining_time INT NULL

La aplicación no ejecuta migraciones. No cambia columnas existentes ni la tabla
de incidentes. CHANGECOMPII guarda NewReelQuantity en quantity; no crea otra columna.

## Suscripciones y ACK

`appsettings.json` utiliza objetos Name/Ack. Los cinco eventos existentes siguen
con Ack=true. LOADCOMPIV, CHANGECOMPII y UNLOADCOMP están en monitorización con
Ack=false; no tienen implementación de ACK. MCALARMON/MCALARMOFF tienen Ack=true.
El Worker aplica la política de ACK de las suscripciones de la sesión también en
los caminos de error. Una alarma con Ack=false se persiste sin contestar.

Los ACK de alarma contienen exactamente EventName_ACK, SeqID, Result, MachineName
y ModuleNo. Usan el stream recibido, el framing compartido y FlushAsync. Se envía
Result=0 después de procesar o Result=1 si falla y se puede responder. Un fallo de
envío posterior a la persistencia no cambia PROCESSED a ERROR ni genera otro ACK.
NeximAckBuilder lanza NeximAckException para eventos sin ACK implementado.

## Simulador

Los escenarios anteriores mantienen su contenido y orden. Después se envía:

| Evento | SeqID | ACK |
| --- | --- | --- |
| PARTSUSAGE | 1001 | Sí |
| PDERROR | 1002 | Sí |
| PRODSTARTED | 1003 | Sí |
| PRODCOMPLETEDII | 1004 | Sí |
| PRODSTOPPED | 1005 | Sí |
| LOADCOMPIV | 1006 | No |
| CHANGECOMPII | 1007 | No |
| UNLOADCOMP | 1008 | No |
| MCALARMON | 1009 | Sí |
| MCALARMOFF | 1010 | Sí |
| Primer KEEPALIVE | 1011 | Sí |

Cada escenario nuevo comprueba su suscripción y su flag antes de enviarse.
Las alarmas esperan hasta 20 segundos y validan los cinco campos del ACK.
KEEPALIVE conserva su intervalo real de 120 segundos.

## Pruebas desde PowerShell

Desde la raíz, con .NET 8 y Python disponibles:

```powershell
dotnet build
dotnet build .\NeximSimulator\NeximSimulator.csproj
dotnet run --project .\tests\NeximBridge.ProtocolTests
dotnet run --project .\tests\NeximBridge.PartsUsageTests
Get-ChildItem .\NeximSimulator\tests\test_*.py | Sort-Object Name | ForEach-Object {
    python $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Fallo: $($_.Name)" }
}
```

El puerto local 30040 debe estar libre. Las pruebas Python arrancan sólo sus propias
instancias del simulador, nunca NeximBridge ni SQL. Deben ejecutarse secuencialmente.
La prueba KEEPALIVE tarda aproximadamente nueve minutos porque utiliza los tiempos
reales. Las demás pruebas incluyen timeouts de ACK de 20 segundos.

Las pruebas C# nuevas verifican los comandos y parámetros SQL sin abrir SQL Server.
El rollback se comprueba con un proveedor ADO.NET de prueba que inyecta un fallo en
el segundo detalle y ejecuta el mismo código transaccional usado en producción.
No sustituye una prueba contra SQL Server con el script aplicado.
Las cinco pruebas históricas opcionales `--sql` escriben datos dentro de transacciones
que revierten; no forman parte de la ejecución sin acceso a SQL.

Verificación realizada: compilación de NeximBridge y NeximSimulator correcta;
448 comprobaciones superadas (290 de eventos/persistencia/ACK, 64 de protocolo,
64 de los escenarios anteriores, 27 de los nuevos escenarios y 3 de KEEPALIVE).
Se verificó KEEPALIVE con el intervalo real de 120 segundos. No se ejecutó el script
SQL ni las cinco pruebas opcionales `--sql`; queda pendiente verificar las nuevas
inserciones contra SQL Server tras la aplicación manual del script. El proyecto
de tests de eventos emitió NU1900 porque la auditoría de vulnerabilidades de NuGet
no estaba disponible; esto no impidió compilar ni ejecutar las pruebas.

## Archivos

Creados para cada prefijo `LoadCompIV`, `ChangeCompII`, `UnloadComp`, `McAlarmOn`,
`McAlarmOff`: `<prefijo>Parser.cs`, `<prefijo>Handler.cs`, `I<prefijo>Repository.cs`,
`NeximRepository.<prefijo>.cs` y `NeximSimulator/<prefijo>Scenario.cs`.
Las alarmas añaden además `McAlarmOnAckSender.cs` y `McAlarmOffAckSender.cs`.

También creados: `RemainingEventFields.cs`, `RemainingEventBatch.cs`,
`RemainingEventProcessor.cs`, `IRawEventRepository.cs`, `NeximEventSubscription.cs`,
el script SQL, este documento, `tests/NeximBridge.PartsUsageTests/RemainingEventsTests.cs`
y `NeximSimulator/tests/test_remaining_events.py`.

Modificados: `Worker.cs`, `Program.cs`, `appsettings.json`, `NeximRepository.cs`,
`NeximAckBuilder.cs`, `NeximSetEventBuilder.cs`, `NeximSessionInitializer.cs`,
`NeximSimulator/Program.cs`, `NeximSimulator/KeepAliveSession.cs`,
los Program.cs y csproj de ambos proyectos de tests, los seis scripts Python
existentes y `TCP_INITIALIZATION.md`.

Retirados tras comprobar sus referencias: `ComponentHandler.cs`,
`AlarmAndRecoveryHandler.cs`, `ProductionHandler.cs` y los métodos genéricos
InsertComponentEvent, InsertProductionEvent e InsertAlarmRecoveryEvent.
Los parsers, handlers y repositorios específicos de los cinco eventos anteriores
se conservan. PCBCHECKOUTVII y RECOVERYUP no se implementan.
