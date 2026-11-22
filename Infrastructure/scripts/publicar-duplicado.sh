#!/bin/bash
# Publica DOS veces el mismo OrderCreated (mismo EventId) en el tópico pedidos-creados.
# Simula lo que pasa cuando un publicador reintenta o SQS entrega un mensaje dos veces.
# Uso (PowerShell 7, desde cualquier carpeta):
#   docker exec <contenedor-localstack> bash /scripts/publicar-duplicado.sh 9001
set -euo pipefail
ID=${1:-9001}
EVENTO="{\"EventId\":\"dup-$ID\",\"OrderId\":$ID,\"UserId\":2,\"Email\":\"user@example.com\",\"Total\":25.50,\"CreatedAt\":\"2026-11-16T00:30:00Z\",\"Items\":[]}"
for VEZ in 1 2; do
  MID=$(awslocal sns publish --region us-east-1 --topic-arn arn:aws:sns:us-east-1:000000000000:pedidos-creados \
    --message "$EVENTO" --query MessageId --output text)
  echo "Publicación $VEZ del evento dup-$ID (pedido $ID): MessageId $MID"
done
