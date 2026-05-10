import amqp, { Channel, ChannelModel } from 'amqplib';
import { logger } from '../index';

let connection: ChannelModel;
let channel: Channel;

export async function connectRabbitMQ(): Promise<void> {
  const maxRetries = 10;
  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    try {
      connection = await amqp.connect(process.env.RABBITMQ_URL!);
      channel    = await connection.createChannel();
      await channel.prefetch(10);
      logger.info('RabbitMQ connected');
      return;
    } catch (err) {
      if (attempt === maxRetries) throw err;
      logger.info(`RabbitMQ not ready, retrying in ${attempt * 2}s (attempt ${attempt}/${maxRetries})...`);
      await new Promise(res => setTimeout(res, attempt * 2000));
    }
  }
}

export async function publish(exchange: string, routingKey: string, message: object): Promise<void> {
  const body = Buffer.from(JSON.stringify(message));
  channel.publish(exchange, routingKey, body, {
    persistent:   true,
    contentType:  'application/json',
    messageId:    crypto.randomUUID(),
    timestamp:    Math.floor(Date.now() / 1000),
  });
}

export async function subscribe(
  queue: string,
  handler: (msg: object) => Promise<void>
): Promise<void> {
  await channel.consume(queue, async (msg) => {
    if (!msg) return;
    try {
      const body = JSON.parse(msg.content.toString());
      await handler(body);
      channel.ack(msg);
    } catch (err) {
      logger.error('Message handler failed', { err, queue });
      channel.nack(msg, false, false); // send to DLQ
    }
  });
}
