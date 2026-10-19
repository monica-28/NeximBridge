# Revisión de conformidad previa al despliegue

Referencia indicada: FUJI CORPORATION, Nexim Host Interface Specification
V2.2.0, No. Nexim-1283. Esta revisión aplica las reglas explícitas de la petición;
el PDF original no está presente en el proyecto y no se afirma una certificación
integral del protocolo ni una validación contra una máquina real.

KEEPALIVE valida exactamente dos campos TAB, nombre literal, ausencia de CR/LF y
SeqID 1..999999. El ACK conserva su formato y utiliza el framing existente.
Se escribe y hace FlushAsync antes de intentar guardar el RAW. Esa persistencia
es best-effort, con token independiente y plazo de cinco segundos; un fallo genera
Warning, no reenvía el ACK y no cierra la conexión. KEEPALIVE no participa en hashes.

Ambos INSERT RAW suministran seq_id como Int32/SqlDbType.Int. Un SeqID inválido
se guarda como NULL, conservando el raw_message exacto. No cambia el esquema ni
la deduplicación SHA-256 del RAW original.

Se añaden sólo los dominios solicitados: LaneNo 1/2; ProductMode 0/1;
BlockCount/BlockSkipCount 0..9999; PDERROR NozzleNo 1..4 y NozzlePitNo 1..32;
UNLOADCOMP RemainingTime 10..999999; BSInfoBit hexadecimal ASCII sin imponer una
longitud. Se mantienen SupplyMethod 0/1 y los códigos de alarma de ocho dígitos
hexadecimales. No se añaden rangos dependientes del modelo para módulo, stage o slot.

La especificación Fuji Nexim Host Interface V2.2.0 suministrada no indica
explícitamente la codificación general de caracteres. NeximBridge usa UTF-8.
Los caracteres ASCII son compatibles; antes de utilizar identificadores no ASCII
en producción debe confirmarse la codificación con Fuji o mediante tráfico real.

Las pruebas de conformidad están integradas en NeximBridge.PartsUsageTests.
Ejecutarlas sin `--sql`. Usan streams, sockets loopback y repositorios simulados;
no realizan migraciones ni acceden a Nexim real.
