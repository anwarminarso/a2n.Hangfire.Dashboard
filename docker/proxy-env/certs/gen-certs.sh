#!/bin/sh
# Generates a local CA and a server certificate for $PUBLIC_HOST into /certs.
# Skips generation when a certificate for the same host already exists.
set -eu

HOST="${PUBLIC_HOST:?PUBLIC_HOST is required}"
cd /certs

if [ -f server.crt ] && [ -f server.key ] && [ -f ca.crt ] && [ "$(cat host.txt 2>/dev/null)" = "$HOST" ]; then
    echo "certs: reusing existing certificate for $HOST"
    exit 0
fi

apk add --no-cache openssl >/dev/null

openssl req -x509 -newkey rsa:2048 -nodes -days 825 -sha256 \
    -keyout ca.key -out ca.crt -subj "/CN=Hangfire proxy-env local CA" \
    -addext "basicConstraints=critical,CA:TRUE" \
    -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null

openssl req -newkey rsa:2048 -nodes -keyout server.key -out server.csr -subj "/CN=$HOST" 2>/dev/null

cat > server.ext <<EOF
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
subjectAltName=DNS:$HOST,DNS:localhost,IP:127.0.0.1
EOF

openssl x509 -req -in server.csr -CA ca.crt -CAkey ca.key -CAcreateserial \
    -days 825 -sha256 -extfile server.ext -out server.crt 2>/dev/null

rm -f server.csr server.ext ca.srl
chmod 644 ca.crt server.crt server.key
echo "$HOST" > host.txt
echo "certs: generated certificate for $HOST"
