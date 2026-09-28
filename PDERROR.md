# PDERROR

Implementación basada en Nexim Host Interface Specification V2.2.0,
apartados 4.52.1 y 4.52.2, páginas 152–154.

## Formato exacto

El cuerpo tiene ocho campos de cabecera más NumList, separados por TAB:

```text
PDERROR\tSeqID\tTime\tLineName\tMachineName\tModuleNo\tLaneNo\tProgramName\tNumList\r
```

Cada registro tiene doce campos y termina en CR, incluido el último:

```text
StageNo\tSlotNo\tPartName\tAVLName\tHeadNo\tHolderNo\tNozzleNo\tNozzlePitNo\tBoardNo\tReference\tErrorCode\tSubErrorCode\r
```

No se admite PanelNo. Time se interpreta como UTC con formato `yyyyMMddHHmmss`.
NumList debe coincidir con los registros presentes. Los códigos de error se
conservan como texto hexadecimal de ocho dígitos; no se deriva ninguna descripción.

## Persistencia y confirmación

Worker guarda el mensaje completo en raw con estado PENDING y pasa su ID a
PdErrorHandler. El handler valida todos los registros antes de persistir el lote.
InsertPdErrorDetails inserta una fila por registro en dbo.nexim_incident_event,
con el mismo raw_event_id, dentro de una transacción. Si falla el lote, se revierte
sin perder el raw guardado anteriormente.

Los campos informados se mapean a sus columnas correspondientes. `panel_no`,
`reel_id`, `feeder_id` y `description` se insertan explícitamente como NULL.
No se agregan columnas ni se cambia el esquema.

Después se actualiza el estado del raw a PROCESSED. Ante errores se intenta
actualizar a ERROR, se registra la excepción y se conserva raw_message.
Si SQL no está disponible para actualizar el estado, también se registra ese
fallo; el raw puede permanecer PENDING. Como en PARTSUSAGE, la actualización del
estado es posterior al commit de los detalles.

Worker envía por el mismo NetworkStream el ACK construido por NeximAckBuilder,
con FlushAsync después de escribir. Se reutiliza el framing existente:
cuatro bytes de longitud big endian (incluye STX y ETX), STX, cuerpo y ETX.

```text
PDERROR_ACK\tSeqID\tResult\tMachineName\tModuleNo\tLaneNo\tProgramName
```

El ACK no contiene PanelNo. Result=0 tras el éxito; Result=1 ante fallos si la
conexión sigue disponible y existen los campos necesarios para identificar el
evento. No se inventa una cabecera incompleta. Un fallo de envío posterior al
procesamiento correcto no provoca otro ACK con Result=1.

## Pruebas desde PowerShell

Se amplió la suite existente para comprobar también que PARTSUSAGE no cambia:

```powershell
dotnet build .\NeximBridge.csproj
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj
```

Para incluir SQL Server con la configuración actual:

```powershell
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj -- --sql
```

Hay 48 casos sin SQL y 50 con SQL. Se comprueban dos detalles, todas sus columnas,
los cuatro NULLs, fechas UTC, rechazo de PanelNo extra, registros inválidos,
estados y errores, y ACK de siete campos para Result=0 y Result=1. Las pruebas
SQL usan las sentencias reales parametrizadas y terminan con ROLLBACK: no
conservan filas ni modifican el esquema, aunque pueden consumir IDs de identidad.
No se conectan a Nexim real ni modifican NeximSimulator.
