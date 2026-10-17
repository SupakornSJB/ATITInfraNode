#!/bin/bash
# Run from the ATITFinalProject-dashboard/ directory.
# Builds the React SPA and copies it into GameServer/wwwroot/dashboard/.
set -e
npm run build
cp -r dist/* ../ATITFinalProject-server/GameServer/wwwroot/dashboard/
echo "Dashboard deployed to GameServer/wwwroot/dashboard/"
