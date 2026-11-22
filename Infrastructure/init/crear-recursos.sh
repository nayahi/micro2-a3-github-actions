#!/bin/bash
# ECommerceGRPC · Clase 4 · se ejecuta solo cuando LocalStack termina de arrancar.
# Crea el tópico SNS, las colas SQS, sus DLQ y las suscripciones (fan-out).
#
#   pedidos-creados (SNS) ──┬──> pedidos-procesar  (SQS) ──(3 fallos)──> pedidos-procesar-dlq   ← la lee la saga (Clase 4)
#                           └──> pedidos-notificar (SQS) ──(3 fallos)──> pedidos-notificar-dlq  ← la lee NotificationService
set -euo pipefail
R=us-east-1
CUENTA=000000000000

TOPICO_ARN=$(awslocal sns create-topic --name pedidos-creados --region $R --query TopicArn --output text)

# Clase 4: pedidos-procesar la lee el orquestador de la saga (OrderService), que coordina Inventario y Pagos.
# VisibilityTimeout = 10: si Pagos no responde, la saga NO borra el mensaje y SQS lo entrega otra vez a los 10 s.
# Después de 3 recepciones sin borrar, el pedido pasa a pedidos-procesar-dlq.
awslocal sqs create-queue --queue-name pedidos-procesar-dlq --region $R > /dev/null
awslocal sqs create-queue --queue-name pedidos-procesar --region $R \
  --attributes '{"VisibilityTimeout":"10","RedrivePolicy":"{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:000000000000:pedidos-procesar-dlq\",\"maxReceiveCount\":\"3\"}"}' > /dev/null

# Clase 3: la cola de NotificationService y su DLQ.
# VisibilityTimeout = 5: un mensaje recibido y NO borrado vuelve a aparecer a los 5 segundos.
awslocal sqs create-queue --queue-name pedidos-notificar-dlq --region $R > /dev/null
URL_NOTIFICAR=$(awslocal sqs create-queue --queue-name pedidos-notificar --region $R \
  --attributes '{"VisibilityTimeout":"5"}' --query QueueUrl --output text)

awslocal sqs set-queue-attributes --region $R --queue-url "$URL_NOTIFICAR" \
  --attributes '{"RedrivePolicy":"{\"deadLetterTargetArn\":\"arn:aws:sqs:us-east-1:000000000000:pedidos-notificar-dlq\",\"maxReceiveCount\":\"3\"}"}'

for COLA in pedidos-procesar pedidos-notificar; do
  awslocal sns subscribe --region $R --topic-arn "$TOPICO_ARN" --protocol sqs \
    --notification-endpoint "arn:aws:sqs:$R:$CUENTA:$COLA" \
    --attributes RawMessageDelivery=true > /dev/null
done

echo "Clase 4 lista: tópico pedidos-creados; colas pedidos-procesar, pedidos-notificar y sus DLQ"
