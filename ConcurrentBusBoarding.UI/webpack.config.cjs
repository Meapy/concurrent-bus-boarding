const path = require("path");

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
        // css-loader only. The CSS text stays inside the bundle and src/index.js injects
        // it as a <style> element: Game.Modding.ModManager registers only the .mjs of a
        // UI module as a UI mod location, so an extracted sibling .css is never loaded
        // and every rule in it is silently dropped.
        test: /\.css$/,
        use: [{
          loader: "css-loader",
          options: {
            modules: { localIdentName: "cbb_[local]_[hash:base64:5]" },
            esModule: true,
            sourceMap: false
          }
        }]
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
  experiments: { outputModule: true }
};
