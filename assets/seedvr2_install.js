// addInstallButton(groupId, featureId, installId, buttonText)
// Place install button in the SeedVR2 group itself.
// Group IDs are derived from group names and only keep lowercase letters:
// "SeedVR2 Upscaler" => "seedvrupscaler"
addInstallButton('seedvrupscaler', 'seedvr2_upscaler', 'seedvr2_upscaler', 'Install SeedVR2 Upscaler Node');

// Add SeedVR2 Upscale button to image/video context menus
(function() {
    // Video file extensions supported by SeedVR2
    let videoExtensions = ['mp4', 'webm', 'gif', 'mov', 'avi', 'mkv'];

    // Wait for buttonsForImage to be defined, then wrap it
    let checkInterval = setInterval(() => {
        if (typeof buttonsForImage === 'function') {
            clearInterval(checkInterval);

            // Store original function
            let originalButtonsForImage = buttonsForImage;

            // Replace with wrapped version
            buttonsForImage = function(fullsrc, src, metadata) {
                // Call original function
                let buttons = originalButtonsForImage(fullsrc, src, metadata);

                // Only add SeedVR2 button if feature is available
                if (typeof currentBackendFeatureSet !== 'undefined' &&
                    currentBackendFeatureSet.includes('seedvr2_upscaler')) {

                    // Check if this is a data URL (drag & dropped image)
                    let isDataImage = src.startsWith('data:');

                    // Determine if this is a video or image based on extension (for file paths)
                    // Data URLs are always images (video data URLs aren't supported)
                    let isVideo = false;
                    if (!isDataImage) {
                        let extension = src.split('.').pop().toLowerCase().split('?')[0];
                        isVideo = videoExtensions.includes(extension);
                    }

                    // Skip data URL videos (not supported), but allow data URL images
                    if (!isVideo || !isDataImage) {
                        buttons.push({
                            label: 'SeedVR2 Upscale',
                            title: 'Upscale this ' + (isVideo ? 'video' : 'image') + ' using SeedVR2 AI upscaler',
                            onclick: (e) => {
                                // Build input overrides with the appropriate file parameter
                                let input_overrides = {
                                    'images': 1
                                };

                                if (isDataImage) {
                                    // For data URLs (drag & dropped images), pass the data URL directly
                                    input_overrides['seedvr2imagefile'] = src;
                                } else {
                                    // For file paths, use getImageOutPrefix() for correct prefix
                                    let prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
                                    let filePath = prefix + '/' + fullsrc;

                                    if (isVideo) {
                                        input_overrides['seedvr2videofile'] = filePath;
                                    } else {
                                        input_overrides['seedvr2imagefile'] = filePath;
                                    }
                                }

                                // Read SeedVR2 params from UI (fixes issue #14)
                                // Note: SwarmUI strips "2" from "SeedVR2" when creating element IDs
                                let seedvr2Params = [
                                    'seedvrmodel', 'seedvrupscaleby', 'seedvrresolution', 'seedvrblockswap',
                                    'seedvrcolorcorrection', 'seedvrtwostepmode', 'seedvrpredownscale', 'seedvrtiledvae',
                                    'seedvrlatentnoisescale', 'seedvrinputnoisescale', 'seedvrcachemodel', 'seedvrattentionmode',
                                    'seedvrvaeoffloaddevice', 'seedvrvideobatchsize', 'seedvrtemporaloverlap', 'seedvruniformbatchsize'
                                ];

                                for (let paramId of seedvr2Params) {
                                    let elem = document.getElementById('input_' + paramId);
                                    if (elem) {
                                        let val = typeof getInputVal === 'function' ? getInputVal(elem, true) : elem.value;
                                        if (val != null && val !== '') {
                                            input_overrides[paramId] = val;
                                        }
                                    }
                                }

                                // Ensure seedvrmodel is always set - use default if nothing else found
                                if (!input_overrides['seedvrmodel']) {
                                    input_overrides['seedvrmodel'] = 'seedvr2-auto';
                                }

                                // Preserve original image metadata (fixes issue #12)
                                if (metadata) {
                                    try {
                                        let readable = typeof interpretMetadata === 'function' ? interpretMetadata(metadata) : metadata;
                                        if (readable) {
                                            let metadataParsed = JSON.parse(readable);
                                            if (metadataParsed.sui_image_params) {
                                                // Params to skip - not used in file upscaling or could cause validation errors
                                                let skipParams = [
                                                    'model', 'refinermodel', 'loras', 'loraweights',  // Models may not exist
                                                    'images', 'swarm_version',  // Special params
                                                    'width', 'height', 'aspectratio', 'sidelength'  // Resolution comes from source image
                                                ];
                                                for (let key in metadataParsed.sui_image_params) {
                                                    let keyLower = key.toLowerCase();
                                                    // Skip SeedVR2 params (use current UI settings) and params in skip list
                                                    if (!keyLower.startsWith('seedvr') &&
                                                        !skipParams.includes(keyLower)) {
                                                        input_overrides[key] = metadataParsed.sui_image_params[key];
                                                    }
                                                }
                                            }
                                        }
                                    } catch (e) {
                                        console.log('SeedVR2: Could not parse original metadata:', e);
                                    }
                                }

                                // Trigger generation with the file parameter
                                if (typeof mainGenHandler !== 'undefined' && mainGenHandler.doGenerate) {
                                    mainGenHandler.doGenerate(input_overrides, {});
                                }
                            }
                        });
                    }
                }

                return buttons;
            };
        }
    }, 100);
})();
