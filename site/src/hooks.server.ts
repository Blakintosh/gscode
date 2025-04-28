

// import * as gscApiT7 from "$lib/apiSource/t7_api_gsc.json";
// import * as cscApiT7 from "$lib/apiSource/t7_api_csc.json";
// import { pool } from "$lib/db";
// import OpenAI from "openai";
// import type { ScrFunction } from "$lib/models/library";
// import pgvector from 'pgvector/pg';

// const openai = new OpenAI({
//     apiKey: process.env.OPENAI_API_KEY
// });

// function getApiVersion(gameId: string, languageId: string) {
//     if(gameId === "t7") {
//         if(languageId === "gsc") {
//             return gscApiT7.revision;
//         } else if(languageId === "csc") {
//             return cscApiT7.revision;
//         }
//     }
//     throw new Error("Not a valid recognised game ID or language ID.");
// }

// async function areEmbeddingsUpToDate(gameId: string, languageId: string, currentRevision: number) {
//     const apiRecords = await pool.query(`
//         SELECT version, COUNT(*) AS embedded_functions
//         FROM LanguageVersion
//         JOIN FunctionEmbeddings ON LanguageVersion.id = FunctionEmbeddings.versionId
//         WHERE language_id = $1 AND game_id = $2
//         GROUP BY version
//         ORDER BY version DESC
//         LIMIT 1
//     `, [languageId, gameId]);

//     if (apiRecords.rows.length === 0) {
//         return false;
//     }

//     const { version, embedded_functions } = apiRecords.rows[0];
//     console.log(`Current revision: ${currentRevision}, Latest revision: ${version}, Number of embeddings: ${embedded_functions}`);
//     return version === currentRevision && embedded_functions > 0;
// }

// type EmbeddingResult = { name: string, rawText: string, embedding: number[] };

// async function generateFunctionEmbedding(apiFunction: ScrFunction): Promise<EmbeddingResult> {
//     const input = `${apiFunction.name}: '${apiFunction.description}'.`;

//     const embedding = await openai.embeddings.create({
//         model: "text-embedding-3-large",
//         input,
//         encoding_format: "float",
//     });

//     return { name: apiFunction.name, rawText: input, embedding: embedding.data[0].embedding };
// }

// export async function updateFunctionEmbeddings(gameId: string, languageId: string) {
//     const currentRevision = getApiVersion(gameId, languageId);
//     const upToDate = await areEmbeddingsUpToDate(gameId, languageId, currentRevision);

//     if (upToDate) {
//         console.log(`Function embeddings for ${languageId} ${gameId} are already up-to-date.`);
//         return;
//     }

//     console.warn(`Function embeddings for ${languageId} ${gameId} are not up-to-date. Updating...`);

//     const api = languageId === "gsc" ? gscApiT7 : cscApiT7;

//     // Generate embeddings for the whole API reference
//     const results: EmbeddingResult[] = [];

//     const apiFunctions = api.api as ScrFunction[];

//     for(let i = 0; i < apiFunctions.length; i++) {
//         const apiFunction = apiFunctions[i];
//         const embedding = await generateFunctionEmbedding(apiFunction);
//         console.log(`Generated embedding for ${embedding.name}. Result ${i + 1}/${apiFunctions.length}.`);

//         results.push(embedding);
//     }

//     // Save embeddings to the database
//     const functionNames = results.map((result) => result.name);
//     const rawTexts = results.map((result) => result.rawText);
//     const embeddings = results.map((result) => pgvector.toSql(result.embedding));

//     await pool.query(`
//         CALL InsertFunctionEmbeddings($1, $2, $3, $4, $5, $6)
//     `, [languageId, gameId, currentRevision, functionNames, rawTexts, embeddings]);

//     console.log(`Function embeddings for ${languageId} ${gameId} have been updated.`);
// }

// // await updateFunctionEmbeddings("t7", "gsc").catch(console.error);
// // await updateFunctionEmbeddings("t7", "csc").catch(console.error);

// async function generateTestEmbedding(input: string): Promise<number[]> {
//     const embedding = await openai.embeddings.create({
//         model: "text-embedding-3-large",
//         input,
//         encoding_format: "float",
//     });

//     return embedding.data[0].embedding;
// }

// async function testEmbeddings() {
//     const embeddings = await generateTestEmbedding("delete a corpse");

//     const result = await pool.query('SELECT fe.function_name, fe.raw_text FROM FunctionEmbeddings fe INNER JOIN LanguageVersion lv ON fe.versionId = lv.id WHERE lv.language_id = \'gsc\' AND lv.game_id = \'t7\' ORDER BY fe.embedding <-> $1 LIMIT 5', [pgvector.toSql(embeddings)]);

//     console.log(result.rows);
// }

// await testEmbeddings();