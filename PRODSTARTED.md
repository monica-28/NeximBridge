# PRODSTARTED

Se procesa mediante ProdStartedParser, ProdStartedHandler,
NeximRepository.ProdStarted y ProdStartedAckSender. Worker utiliza esta ruta
específica; PRODSTARTED no pasa por el parser genérico de ProductionHandler.

El mensaje debe tener exactamente diez campos TAB, sin CR, NumList ni detalles:

```text
PRODSTARTED\tSeqID\tTime\tLineName\tMachineName\tModuleNo\tLaneNo\tProductMode\tProgramName\tPanelNo
```

Time se interpreta como UTC con formato `yyyyMMddHHmmss`. ProductMode es el
campo de índice 7; ProgramName y PanelNo son los índices 8 y 9. Los campos numéricos
se validan sin depender de la configuración regional.

Worker inserta primero el raw como PENDING. El handler inserta exactamente una
fila en dbo.nexim_production_event con ese raw_event_id y marca el raw PROCESSED.
Si falla el parsing o la persistencia, intenta marcar ERROR y conserva el mensaje
raw. Si SQL no permite actualizar el estado, registra también ese error. La fila
normalizada y la actualización del estado son operaciones separadas, igual que
en los handlers existentes: un fallo de estado no revierte una fila ya guardada.

No se insertan seq_id ni raw_message en producción. cycle_time, block_count,
block_skip_count y bs_info_bit se insertan como NULL. status también queda NULL:
no se recibe del protocolo y el código anterior no establecía para esa columna
el significado interno PROCESSED. La trazabilidad del procesamiento está en
nexim_raw_event.parse_status.

El ACK reutiliza NeximAckBuilder y el framing existente, se escribe en el mismo
NetworkStream y se finaliza con FlushAsync:

```text
PRODSTARTED_ACK\t1003\t0\tNXT1\t1\t1\t0\tTESTJOB_T\t10002
```

Result=0 tras el éxito; Result=1 ante fallos si la conexión y los campos de
identificación necesarios están disponibles. No se inventan campos ausentes.
Un fallo de envío posterior al procesamiento no genera un segundo ACK negativo.

## Pruebas desde PowerShell

Desde la raíz del proyecto:

```powershell
dotnet build .\NeximBridge.csproj
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj
```

La suite existente incluye ahora los tests de PRODSTARTED y las regresiones de
PARTSUSAGE y PDERROR: 66 pruebas sin SQL. Para incluir tres pruebas SQL adicionales:

```powershell
dotnet run --project .\tests\NeximBridge.PartsUsageTests\NeximBridge.PartsUsageTests.csproj -- --sql
```

Las pruebas SQL ejecutan las mismas sentencias parametrizadas del repositorio en
una transacción con ROLLBACK. No dejan filas ni alteran el esquema, aunque pueden
consumir valores IDENTITY. Verifican todos los valores, los NULLs, una única fila
de producción y la conservación del raw al actualizar su estado.

No se han implementado cambios para PRODCOMPLETEDII ni PRODSTOPPED, ni modificado
el simulador o los flujos de PARTSUSAGE, PDERROR, KEEPALIVE, SETEV y STARTEV.
