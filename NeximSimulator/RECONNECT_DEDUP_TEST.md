# Retransmisión tras reconexión

Desde la raíz del proyecto, en PowerShell:

```powershell
dotnet build .\NeximSimulator\NeximSimulator.csproj --no-restore
dotnet run --project .\NeximSimulator --no-build -- --reconnect-dedup-test
```

Arrancar NeximBridge con su configuración local existente. El simulador escucha
en 127.0.0.1:30040 y exige PARTSUSAGE con ACK=1 en ambas inicializaciones.
Tras el primer ACK correcto cierra la conexión, mantiene el listener y espera
la reconexión de Bridge. Reenvía el mismo frame guardado, sin regenerar Time.
Tras el segundo ACK correcto cierra la conexión y termina. No envía otros eventos
ni KEEPALIVE. Un ACK incorrecto o un timeout de ACK de 20 segundos termina con
código de salida 1. Ctrl+C cancela la espera de reconexión.

`RECONNECT DEDUP TEST PASSED` confirma las dos inicializaciones, transmisiones
y respuestas correctas. Para comprobar la deduplicación real, revisar además los
logs de NeximBridge: la segunda recepción debe identificarse como duplicado y
no volver a procesar los detalles. El simulador no consulta ni modifica SQL.

Pruebas automatizadas con cliente TCP local simulado (sin SQL):

```powershell
python .\NeximSimulator\tests\test_reconnect_dedup_mode.py
python .\NeximSimulator\tests\test_dedup_mode.py
python .\NeximSimulator\tests\test_remaining_events.py
```

Ejecutarlas secuencialmente con el puerto 30040 libre. Se comparan los frames
completos capturados en dos conexiones diferentes, dejando pasar más de un
segundo entre sesiones para detectar una regeneración accidental del timestamp.
Los modos normal y `--dedup-test` conservan sus escenarios existentes.
