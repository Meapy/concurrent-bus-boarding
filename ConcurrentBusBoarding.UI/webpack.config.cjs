const path = require("path");
const MiniCssExtractPlugin = require("mini-css-extract-plugin");

module.exports = {
  mode: "production",
  entry: "./src/index.js",
  externalsType: "window",
  externals: {
    react: "React",
    "cs2/api": "cs2/api"
  },
  module: {
    rules: [
      {
        test: /\.css$/,
        use: [MiniCssExtractPlugin.loader, { loader: "css-loader", options: { modules: true } }]
      }
    ]
  },
  resolveLoader: {
    modules: [path.resolve(__dirname, "node_modules"), ...(process.env.NODE_PATH || "").split(path.delimiter).filter(Boolean)]
  },
  output: {
    path: path.resolve(__dirname, "dist"),
    filename: "ConcurrentBusBoarding.mjs",
    library: { type: "module" },
    publicPath: "coui://ui-mods/",
    clean: true
  },
  plugins: [new MiniCssExtractPlugin({ filename: "ConcurrentBusBoarding.css" })],
  experiments: { outputModule: true }
};
