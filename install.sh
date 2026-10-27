#!/usr/bin/env sh
set -eu

if [ "$#" -ne 1 ]; then
  echo "Usage: ./install.sh <domain-or-public-ip>"
  echo "Example: ./install.sh 43.139.208.28"
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "Docker is required. Install Docker Engine and run this script again."
  exit 1
fi

domain="$1"
printf 'DUOTRACK_DOMAIN=%s\n' "$domain" > .env
chmod 600 .env
docker compose up -d --build
echo "DuoTrack is starting at https://$domain"
echo "Check it with: curl https://$domain/health"
