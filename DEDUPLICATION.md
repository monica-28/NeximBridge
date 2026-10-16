# Deduplicación de retransmisiones exactas

Se aplica a PARTSUSAGE, PDERROR, PRODSTARTED, PRODCOMPLETEDII, PRODSTOPPED,
LOADCOMPIV, CHANGECOMPII, UNLOADCOMP, MCALARMON y MCALARMOFF.

`RawEventDeduplication` calcula SHA-256 de los bytes UTF-8 del `RawMessage`
original. No usa Fields, ni normaliza TAB, CR, LF, espacios o mayúsculas.
El resultado tiene 32 bytes y se guarda en la columna existente raw_hash.
received_at no participa en el hash. Cambiar Time o cualquier detalle produce
un evento distinto aunque conserve EventName y SeqID.

## Persistencia y concurrencia

`NeximRepository.InsertRawEventOrGetExistingAsync` intenta primero el INSERT
con PENDING y raw_hash. El índice UNIQUE existente arbitra inserciones concurrentes;
no hay un SELECT previo ni una caché local que actúe como garantía de unicidad.

Sólo ante SqlException 2601/2627 se consulta por hash. Se devuelve el identificador
y el estado existentes sin ejecutar UPDATE. Si no aparece el hash, se propaga la
excepción original: el conflicto podría corresponder a otra restricción UNIQUE.
Se compara también raw_message de forma ordinal y EventName; una coincidencia de
hash con contenido diferente se rechaza, nunca se confirma como procesada.

No se modifica el esquema ni se generan migraciones. Los RAW históricos con
raw_hash=NULL siguen así; la protección comienza con los nuevos RAW que guarde
esta versión. KEEPALIVE conserva su inserción anterior con estado OK, sin hash.
SETEV, STARTEV y otros controles no entran en el procesador de deduplicación.

## Procesamiento y ACK

El Worker y RemainingEventProcessor delegan en `NeximEventProcessor` antes de
llamar al handler específico. Los parsers, handlers y comandos de persistencia
normalizada existentes no se modifican.

| Resultado RAW | Handler | Estado RAW | ACK si Ack=true |
| --- | --- | --- | --- |
| Nuevo | Flujo existente | PROCESSED o ERROR según resultado | 0 o 1 según resultado |
| Duplicado PROCESSED | No se ejecuta | Sin cambios | Result=0 |
| Duplicado PENDING/ERROR | No se ejecuta | Sin cambios | Result=1 |
| Duplicado con estado desconocido/NULL | No se ejecuta | Sin cambios | Result=1 |

Ack=false suprime todas las respuestas. Se reutilizan los AckSender específicos
con el mensaje recibido actualmente y el mismo stream. No se reconstruyen ACK
genéricos. Un fallo de envío no cambia el RAW ni vuelve a ejecutar el handler.
Los componentes continúan configurados como monitorización sin ACK.

Los estados desconocidos se tratan conservadoramente igual que PENDING/ERROR.
Si una retransmisión coincide con un procesamiento todavía activo, puede recibir
Result=1; una retransmisión posterior a PROCESSED recibirá Result=0. No hay
recuperación automática de eventos pendientes ni erróneos.

Los logs nuevos indican EventName, SeqID, RawEventId y estado. No contienen
raw_message completo. Los formatos wire de eventos, ACK y framing se conservan.

## Archivos de esta implementación

Creados:

- RawEventDeduplication.cs
- NeximRepository.RawDeduplication.cs
- NeximEventProcessor.cs
- tests/NeximBridge.PartsUsageTests/DeduplicationTests.cs
- DEDUPLICATION.md

Modificados:

- IRawEventRepository.cs
- Worker.cs
- RemainingEventProcessor.cs
- Program.cs
- tests/NeximBridge.PartsUsageTests/Program.cs
- tests/NeximBridge.PartsUsageTests/RemainingEventsTests.cs
- tests/NeximBridge.PartsUsageTests/NeximBridge.PartsUsageTests.csproj

NeximSimulator, SETEV, STARTEV, KEEPALIVE, los parsers, los mapeos SQL normalizados
y las clases de ACK/framing permanecen intactos.

## Pruebas desde PowerShell

Desde la raíz del proyecto, con el puerto local 30040 libre:

```powershell
dotnet build
dotnet run --project .\tests\NeximBridge.ProtocolTests
dotnet run --project .\tests\NeximBridge.PartsUsageTests
Get-ChildItem .\NeximSimulator\tests\test_*.py |
    Where-Object Name -ne 'test_keepalive_session.py' |
    Sort-Object Name | ForEach-Object {
        python $_.FullName
        if ($LASTEXITCODE -ne 0) { throw "Fallo: $($_.Name)" }
    }
```

Las pruebas del repositorio ejecutan el código real con un transporte ADO.NET de
prueba. Inyectan instancias reales de Microsoft.Data.SqlClient.SqlException
2601/2627 en el segundo INSERT y verifican INSERT → INSERT → SELECT, el mismo id
y un único RAW. También cubren veinte inserciones concurrentes, mensajes diferentes,
RAW históricos, estados conservadores, ACK perdidos y el PartsUsageHandler real.
No escriben en SQL Server. El índice real se considera existente según el esquema
proporcionado; no se valida aquí contra una instancia SQL.

No ejecutar las cinco pruebas opcionales históricas `--sql` para esta verificación:
requieren escrituras en SQL Server. Se omite la prueba larga de KEEPALIVE de 120
segundos; las pruebas Python restantes incluyen timeouts de ACK de 20 segundos.

Verificación realizada: build correcto, 429/429 pruebas de eventos y deduplicación,
64/64 de protocolo y 91/91 Python; total 584 comprobaciones superadas. No se ejecutó
ninguna migración ni se escribió en SQL Server. La auditoría de vulnerabilidades
NuGet del proyecto de tests emitió NU1900 por indisponibilidad del servicio; la
compilación y las pruebas finalizaron correctamente.
