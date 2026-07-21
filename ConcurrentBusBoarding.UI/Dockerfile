FROM node:22-alpine
WORKDIR /src
COPY package*.json ./
RUN npm ci
COPY . .
CMD ["npm", "test"]
