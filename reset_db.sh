#!/bin/bash

echo "Stopping bot to prevent database access during reset..."
docker compose stop bot

echo "Flushing Redis..."
docker exec economy_redis redis-cli FLUSHALL

echo "Resetting Postgres Database..."
docker exec economy_postgres psql -U bot_user -d economy_db -c "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"

echo "Starting bot back up..."
docker compose start bot

echo "Database and Redis have been successfully reset!"
