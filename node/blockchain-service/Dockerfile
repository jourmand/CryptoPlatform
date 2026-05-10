FROM node:20-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm config set strict-ssl false && npm install
COPY . .
RUN npm run build

FROM node:20-alpine AS final
WORKDIR /app
COPY package*.json ./
RUN npm config set strict-ssl false && npm install --omit=dev
COPY --from=build /app/dist ./dist
USER node
CMD ["node", "dist/index.js"]
