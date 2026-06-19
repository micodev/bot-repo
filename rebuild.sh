#!/bin/bash

echo "🚀 Stopping existing containers..."
docker-compose down

echo "📦 Rebuilding bot container..."
docker-compose build bot

echo "🟢 Starting containers..."
docker-compose up -d

echo "✅ Rebuild complete! Bot is now running."
docker-compose logs -f bot
