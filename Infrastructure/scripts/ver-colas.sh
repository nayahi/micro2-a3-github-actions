#!/bin/bash
# Cuántos mensajes hay en cada cola: visibles (esperando) y en proceso (recibidos, sin borrar).
# Uso: docker exec <contenedor-localstack> bash /scripts/ver-colas.sh
for COLA in pedidos-procesar pedidos-procesar-dlq pedidos-notificar pedidos-notificar-dlq; do
  URL=http://localhost:4566/000000000000/$COLA
  V=$(awslocal sqs get-queue-attributes --region us-east-1 --queue-url $URL \
      --attribute-names ApproximateNumberOfMessages ApproximateNumberOfMessagesNotVisible \
      --query 'Attributes.[ApproximateNumberOfMessages,ApproximateNumberOfMessagesNotVisible]' --output text)
  printf "%-24s visibles %s · en proceso %s\n" "$COLA" $(echo $V)
done
